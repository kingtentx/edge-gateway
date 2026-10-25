using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>Bounded, asynchronous JSON-line logging; acquisition never performs filesystem I/O.</summary>
public sealed partial class GatewayLog : IConfigurableGatewayLog, IGatewayLogStatus, IGatewayLogArchive, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Queue<GatewayLogEntry> _entries = new();
    private readonly int _capacity;
    private readonly string? _directory;
    private readonly Channel<LogWork>? _writes;
    private readonly Task? _writerTask;
    private GatewayLoggingConfiguration _configuration = new();
    private string? _fileError;
    private long _droppedEntries;
    private long _failedEntries;
    private long _pendingEntries;
    private long _pendingBytes;
    private long _entryBytes;
    private int _disposed;
    private const long MaximumQueuedBytes = 64L * 1024 * 1024;
    private const long MaximumMemoryLogBytes = 4L * 1024 * 1024;
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public GatewayLog(string? directory = null, int capacity = 2000, int fileQueueCapacity = 8192,
        Func<string, long>? availableDiskBytes = null, TimeProvider? timeProvider = null)
    {
        _directory = directory is null ? null : Path.GetFullPath(directory);
        _diskProbe = availableDiskBytes ?? ReadAvailableDiskBytes;
        _clock = timeProvider ?? TimeProvider.System;
        _capacity = Math.Clamp(capacity, 100, 10000);
        if (_directory is null) return;
        _writes = Channel.CreateBounded<LogWork>(new BoundedChannelOptions(Math.Clamp(fileQueueCapacity, 1, 65536))
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public long DroppedEntries => Interlocked.Read(ref _droppedEntries);
    public long FailedEntries => Interlocked.Read(ref _failedEntries);
    public long PendingEntries => Interlocked.Read(ref _pendingEntries);
    public long PendingBytes => Interlocked.Read(ref _pendingBytes);
    public string? FileError { get { lock (_sync) return _fileError; } }
    public GatewayLoggingStatus GetStatus() => new(PendingEntries, DroppedEntries, FailedEntries, FileError)
    {
        DirectoryPath = _directory, SampleDirectoryPath = _directory is null ? null : Path.Combine(_directory, "data"),
        PendingBytes = PendingBytes, AvailableDiskBytes = Interlocked.Read(ref _diskFreeBytes) is var free && free >= 0 ? free : null,
        DiskProtectionActive = Volatile.Read(ref _diskPaused) != 0, DiskRejectedEntries = Interlocked.Read(ref _diskRejected)
    };

    public void ApplyConfiguration(GatewayLoggingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Enum.IsDefined(configuration.MinimumLevel) || configuration.MaxFileSizeMb is < 1 or > 1024
            || configuration.RetainedFileCount is < 1 or > 1000 || configuration.RetainedDays is < 1 or > 3650
            || configuration.MinimumFreeDiskSpaceMb is < 16 or > 1048576 || configuration.DiskResumeMarginMb is < 1 or > 65536)
            throw new ArgumentException("日志级别或文件策略无效；单文件 1–1024 MB，保留 1–1000 份及 1–3650 天。", nameof(configuration));
        Volatile.Write(ref _configuration, new GatewayLoggingConfiguration
        {
            MinimumLevel = configuration.MinimumLevel,
            LogAcquisitionSamples = configuration.LogAcquisitionSamples,
            MaxFileSizeMb = configuration.MaxFileSizeMb,
            RetainedFileCount = configuration.RetainedFileCount,
            RetainedDays = configuration.RetainedDays,
            MinimumFreeDiskSpaceMb = configuration.MinimumFreeDiskSpaceMb,
            DiskResumeMarginMb = configuration.DiskResumeMarginMb
        });
        // Wake the existing writer without doing filesystem I/O on the caller/UI thread.
        // If the queue is full, the next accepted entry/flush uses the current policy anyway.
        if (Volatile.Read(ref _disposed) == 0)
            _writes?.Writer.TryWrite(new LogWork(null, null, Volatile.Read(ref _configuration), null, 0, RefreshPolicy: true));
    }

    public void Write(GatewayLogLevel level, string message)
    {
        var configuration = Volatile.Read(ref _configuration);
        if (level < configuration.MinimumLevel) return;
        var bytes = 512 + message.Length * 6L;
        if (bytes > SamplePayloadBudget.MaximumSampleBytes)
        { Reject("单条日志超过 1 M 预估载荷上限，已拒绝该条记录。"); return; }
        var entry = new GatewayLogEntry(DateTimeOffset.UtcNow, level, message.Replace('\r', ' ').Replace('\n', ' '));
        lock (_sync) Append(entry);
        Queue(new LogWork(entry, null, configuration, null, bytes));
    }

    public void WriteSample(TelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var configuration = Volatile.Read(ref _configuration);
        if (!configuration.LogAcquisitionSamples || configuration.MinimumLevel > GatewayLogLevel.Information) return;
        if (_writes is null) return;
        if (!SamplePayloadBudget.TryEstimate(sample, out var bytes))
        { Reject("单条采样超出 1 M 或结构复杂度限制，未写入采集日志。"); return; }
        var entry = new GatewayLogEntry(DateTimeOffset.UtcNow, GatewayLogLevel.Information, "");
        // Samples go to structured files; preserve the small UI ring for operational events.
        Queue(new LogWork(entry, sample, configuration, null, bytes));
    }

    private void Queue(LogWork work)
    {
        if (_writes is null) return;
        while (true)
        {
            var pending = Interlocked.Read(ref _pendingBytes);
            if (pending + work.EstimatedBytes > MaximumQueuedBytes)
            { Reject("日志后台队列已达 64 M 预估载荷配额，本条日志未入队。"); return; }
            if (Interlocked.CompareExchange(ref _pendingBytes, pending + work.EstimatedBytes, pending) == pending) break;
        }
        Interlocked.Increment(ref _pendingEntries);
        if (Volatile.Read(ref _disposed) == 0 && _writes.Writer.TryWrite(work)) return;
        Interlocked.Decrement(ref _pendingEntries);
        Interlocked.Add(ref _pendingBytes, -work.EstimatedBytes);
        Reject("日志后台队列已满或正在关闭，本条日志未写入文件。累计丢弃数量可在日志状态中查看。");
    }

    private void Reject(string error) { Interlocked.Increment(ref _droppedEntries); SetFileError(error); }

    public IReadOnlyList<GatewayLogEntry> GetEntries(int limit = 500)
    {
        lock (_sync) return _entries.Reverse().Take(Math.Clamp(limit, 1, _capacity)).ToArray();
    }

    /// <summary>Wait for accepted entries and force the current file to disk; errors are returned to the caller.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_writes is null) return;
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_writerTask is not null) await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writes.Writer.WriteAsync(new LogWork(null, null, Volatile.Read(ref _configuration), barrier, 0), cancellationToken).ConfigureAwait(false);
        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        var destinations = new[] { new LogFileState(_directory!), new LogFileState(Path.Combine(_directory!, "data")) };
        void MaintainCurrentPolicy(LogFileState state, GatewayLoggingConfiguration policy, string path, bool force = false)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (!force && ReferenceEquals(state.AppliedPolicy, policy) && today == state.LastMaintenance) return;
            Directory.CreateDirectory(state.Directory);
            MaintainFiles(path, policy);
            state.AppliedPolicy = policy; state.LastMaintenance = today;
            _diskPolicy = null;
        }
        string CurrentPath(LogFileState state) => state.ActivePath ?? DailyLogPath(state.Directory, DateTimeOffset.UtcNow);
        try
        {
            while (await _writes!.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_writes.Reader.TryRead(out var work))
                {
                    if (work.RefreshPolicy)
                    {
                        try
                        {
                            var current = Volatile.Read(ref _configuration);
                            foreach (var destination in destinations) MaintainCurrentPolicy(destination, current, CurrentPath(destination));
                            CanWriteToDisk(0, current);
                        }
                        catch (Exception exception) when (IsLoggingFailure(exception))
                        { SetFileError($"日志保留策略应用失败：{exception.Message}"); }
                        continue;
                    }
                    if (work.Barrier is not null)
                    {
                        try
                        {
                            foreach (var destination in destinations)
                                if (destination.Stream is not null)
                                {
                                    await destination.Stream.FlushAsync().ConfigureAwait(false);
                                    destination.Stream.Flush(flushToDisk: true);
                                }
                            work.Barrier.TrySetResult();
                        }
                        catch (Exception exception) when (IsLoggingFailure(exception))
                        { SetFileError($"日志刷新失败：{exception.Message}"); work.Barrier.TrySetException(exception); }
                        continue;
                    }
                    var state = destinations[work.Sample is null ? 0 : 1];
                    try
                    {
                        var entry = work.Entry!;
                        if (work.Sample is not null && !SamplePayloadBudget.TryEstimate(work.Sample, out _))
                            throw new InvalidDataException("排队期间采样载荷变化，超出 1 M 或结构复杂度限制。");
                        var bytes = SerializeLine(entry, work.Sample);
                        var policy = Volatile.Read(ref _configuration);
                        var limit = (long)policy.MaxFileSizeMb * 1024 * 1024;
                        if (bytes.LongLength > limit)
                            throw new InvalidDataException($"单条日志超过单文件 {policy.MaxFileSizeMb} MB 上限，本条未写入。");
                        var path = DailyLogPath(state.Directory, entry.Timestamp);
                        if (state.ActivePath != path)
                        {
                            if (state.Stream is not null) await state.Stream.DisposeAsync().ConfigureAwait(false);
                            state.Stream = null;
                            state.ActivePath = path;
                        }
                        // Retention must run BEFORE disk protection, otherwise expired logs can
                        // keep the writer permanently paused even though it could reclaim space.
                        MaintainCurrentPolicy(state, policy, path);
                        if (!CanWriteToDisk(bytes.Length, policy))
                        {
                            Interlocked.Increment(ref _diskRejected);
                            Interlocked.Increment(ref _droppedEntries);
                            continue;
                        }
                        state.Stream ??= OpenLogFile(path);
                        if (state.Stream.Length + bytes.LongLength > limit)
                        {
                            await state.Stream.DisposeAsync().ConfigureAwait(false);
                            state.Stream = null;
                            var rotated = NextRotatedLogPath(path);
                            File.Move(path, rotated);
                            state.Stream = OpenLogFile(path);
                            MaintainCurrentPolicy(state, policy, path, force: true);
                        }
                        await state.Stream.WriteAsync(bytes).ConfigureAwait(false);
                        _bytesSinceDiskCheck += bytes.Length;
                        if (++state.Unflushed >= 128)
                        {
                            await state.Stream.FlushAsync().ConfigureAwait(false);
                            state.Unflushed = 0;
                        }
                        ClearFileError();
                    }
                    catch (Exception exception) when (IsLoggingFailure(exception))
                    {
                        Interlocked.Increment(ref _failedEntries);
                        Interlocked.Increment(ref _droppedEntries);
                        SetFileError($"日志文件写入失败，本条未保存：{exception.Message}");
                        if (state.Stream is not null)
                        {
                            try { await state.Stream.DisposeAsync().ConfigureAwait(false); } catch (Exception closeError) when (IsLoggingFailure(closeError)) { }
                            state.Stream = null;
                        }
                    }
                    finally { Interlocked.Decrement(ref _pendingEntries); Interlocked.Add(ref _pendingBytes, -work.EstimatedBytes); }
                }
                foreach (var state in destinations)
                {
                    if (state.Stream is null || state.Unflushed == 0) continue;
                    try { await state.Stream.FlushAsync().ConfigureAwait(false); state.Unflushed = 0; }
                    catch (Exception exception) when (IsLoggingFailure(exception)) { SetFileError($"日志刷新失败：{exception.Message}"); }
                }
            }
        }
        finally
        {
            foreach (var state in destinations)
            {
                if (state.Stream is null) continue;
                try { await state.Stream.FlushAsync().ConfigureAwait(false); state.Stream.Flush(flushToDisk: true); }
                catch (Exception exception) when (IsLoggingFailure(exception)) { SetFileError($"退出时日志刷新失败：{exception.Message}"); }
                try { await state.Stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (IsLoggingFailure(exception)) { SetFileError($"关闭日志文件失败：{exception.Message}"); }
            }
        }
    }

    private sealed class LogFileState(string directory)
    {
        public string Directory { get; } = directory;
        public FileStream? Stream;
        public string? ActivePath;
        public GatewayLoggingConfiguration? AppliedPolicy;
        public DateOnly LastMaintenance = DateOnly.MinValue;
        public int Unflushed;
    }

    private static FileStream OpenLogFile(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    private static byte[] SerializeLine(GatewayLogEntry entry, TelemetrySample? sample)
    {
        var content = sample is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { timestamp = entry.Timestamp, level = entry.Level, kind = "event", message = entry.Message }, LineOptions)
            : JsonSerializer.SerializeToUtf8Bytes(new SampleLogLine(sample.Timestamp.ToUniversalTime(), sample.DeviceId, sample.DeviceName,
                sample.TagId, sample.TagName, sample.Address, sample.RawValue, sample.Value, sample.Quality,
                string.IsNullOrWhiteSpace(sample.Unit) ? null : sample.Unit,
                string.IsNullOrWhiteSpace(sample.Error) ? null : sample.Error), LineOptions);
        var line = new byte[content.Length + 1];
        content.CopyTo(line, 0); line[^1] = (byte)'\n';
        return line;
    }

    // Keep this file format separate from TelemetrySample's API/MQTT/database serialization.
    // Null raw/value fields carry meaning for failed reads and must remain explicit.
    private sealed record SampleLogLine(DateTimeOffset Timestamp, Guid DeviceId, string DeviceName,
        Guid TagId, string TagName, string Address, object? RawValue, object? Value, SampleQuality Quality,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error);

    private static string DailyLogPath(string directory, DateTimeOffset timestamp) =>
        Path.Combine(directory, timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    private static string NextRotatedLogPath(string path)
    {
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        for (var number = 1; ; number++)
        {
            var candidate = stem + "." + number.ToString("D3", CultureInfo.InvariantCulture) + ".log";
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static IEnumerable<string> EnumerateLogFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith("gateway-", StringComparison.Ordinal)
                || DailyLogFileName().IsMatch(Path.GetFileName(path)));

    [GeneratedRegex(@"\A[0-9]{8}(?:\.[0-9]{3,})?\.log\z", RegexOptions.CultureInvariant)]
    private static partial Regex DailyLogFileName();

    private void MaintainFiles(string activePath, GatewayLoggingConfiguration configuration)
    {
        var cutoff = DateTime.UtcNow.AddDays(-configuration.RetainedDays);
        var files = EnumerateLogFiles(Path.GetDirectoryName(activePath)!).Select(path => new FileInfo(path)).ToArray();
        foreach (var file in files.Where(file => file.FullName != activePath && file.LastWriteTimeUtc < cutoff)) file.Delete();
        var retained = files.Where(file => file.FullName != activePath && File.Exists(file.FullName))
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal).ToArray();
        foreach (var file in retained.Skip(configuration.RetainedFileCount - 1)) file.Delete();
    }

    private void SetFileError(string error)
    {
        lock (_sync)
        {
            if (_fileError != error) Append(new GatewayLogEntry(DateTimeOffset.UtcNow, GatewayLogLevel.Error, error));
            _fileError = error;
        }
    }
    private void ClearFileError() { lock (_sync) _fileError = null; }
    private void Append(GatewayLogEntry entry)
    {
        _entries.Enqueue(entry);
        _entryBytes += 128 + entry.Message.Length * 2L;
        while (_entries.Count > _capacity || _entryBytes > MaximumMemoryLogBytes)
        {
            var removed = _entries.Dequeue();
            _entryBytes -= 128 + removed.Message.Length * 2L;
        }
    }
    private static bool IsLoggingFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException or InvalidOperationException;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _writes?.Writer.TryComplete();
        if (_writerTask is not null) await _writerTask.ConfigureAwait(false);
    }
    private sealed record LogWork(GatewayLogEntry? Entry, TelemetrySample? Sample,
        GatewayLoggingConfiguration Configuration, TaskCompletionSource? Barrier, long EstimatedBytes, bool RefreshPolicy = false);
}
