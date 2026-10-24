using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>Bounded, asynchronous JSON-line logging; acquisition never performs filesystem I/O.</summary>
public sealed class GatewayLog : IConfigurableGatewayLog, IGatewayLogStatus, IAsyncDisposable
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

    public GatewayLog(string? directory = null, int capacity = 2000, int fileQueueCapacity = 8192)
    {
        _directory = directory is null ? null : Path.GetFullPath(directory);
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
    public GatewayLoggingStatus GetStatus() => new(PendingEntries, DroppedEntries, FailedEntries, FileError);

    public void ApplyConfiguration(GatewayLoggingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Enum.IsDefined(configuration.MinimumLevel) || configuration.MaxFileSizeMb is < 1 or > 1024
            || configuration.RetainedFileCount is < 1 or > 1000 || configuration.RetainedDays is < 1 or > 3650)
            throw new ArgumentException("日志级别或文件策略无效；单文件 1–1024 MB，保留 1–1000 份及 1–3650 天。", nameof(configuration));
        Volatile.Write(ref _configuration, new GatewayLoggingConfiguration
        {
            MinimumLevel = configuration.MinimumLevel,
            LogAcquisitionSamples = configuration.LogAcquisitionSamples,
            MaxFileSizeMb = configuration.MaxFileSizeMb,
            RetainedFileCount = configuration.RetainedFileCount,
            RetainedDays = configuration.RetainedDays
        });
    }

    public void Write(GatewayLogLevel level, string message)
    {
        var configuration = Volatile.Read(ref _configuration);
        if (level < configuration.MinimumLevel) return;
        var bytes = 512 + message.Length * 6L;
        if (bytes > SamplePayloadBudget.MaximumSampleBytes)
        { Reject("单条日志超过 1 MiB 预估载荷上限，已拒绝该条记录。"); return; }
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
        { Reject("单条采样超出 1 MiB 或结构复杂度限制，未写入采集日志。"); return; }
        var entry = new GatewayLogEntry(DateTimeOffset.UtcNow, GatewayLogLevel.Information,
            $"采集：设备 [{sample.DeviceName}] 点位 [{sample.TagName}]，质量 {sample.Quality}。".Replace('\r', ' ').Replace('\n', ' '));
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
            { Reject("日志后台队列已达 64 MiB 预估载荷配额，本条日志未入队。"); return; }
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
        FileStream? stream = null;
        string? activePath = null;
        GatewayLoggingConfiguration? appliedPolicy = null;
        var lastMaintenance = DateOnly.MinValue;
        var unflushed = 0;
        try
        {
            while (await _writes!.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_writes.Reader.TryRead(out var work))
                {
                    if (work.Barrier is not null)
                    {
                        try
                        {
                            if (stream is not null)
                            {
                                await stream.FlushAsync().ConfigureAwait(false);
                                stream.Flush(flushToDisk: true);
                            }
                            work.Barrier.TrySetResult();
                        }
                        catch (Exception exception) when (IsLoggingFailure(exception))
                        { SetFileError($"日志刷新失败：{exception.Message}"); work.Barrier.TrySetException(exception); }
                        continue;
                    }
                    try
                    {
                        var entry = work.Entry!;
                        if (work.Sample is not null && !SamplePayloadBudget.TryEstimate(work.Sample, out _))
                            throw new InvalidDataException("排队期间采样载荷变化，超出 1 MiB 或结构复杂度限制。");
                        var bytes = SerializeLine(entry, work.Sample);
                        var limit = (long)work.Configuration.MaxFileSizeMb * 1024 * 1024;
                        if (bytes.LongLength > limit)
                            throw new InvalidDataException($"单条日志超过单文件 {work.Configuration.MaxFileSizeMb} MB 上限，本条未写入。");
                        Directory.CreateDirectory(_directory!);
                        var path = Path.Combine(_directory!, $"gateway-{entry.Timestamp:yyyyMMdd}.log");
                        if (activePath != path)
                        {
                            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                            stream = null;
                            activePath = path;
                        }
                        stream ??= OpenLogFile(path);
                        if (stream.Length + bytes.LongLength > limit)
                        {
                            await stream.DisposeAsync().ConfigureAwait(false);
                            stream = null;
                            var rotated = Path.Combine(_directory!, $"gateway-{entry.Timestamp:yyyyMMdd}-{DateTimeOffset.UtcNow:HHmmssfff}-{Guid.NewGuid():N}.log");
                            File.Move(path, rotated);
                            stream = OpenLogFile(path);
                            appliedPolicy = null;
                        }
                        var today = DateOnly.FromDateTime(DateTime.UtcNow);
                        if (!ReferenceEquals(appliedPolicy, work.Configuration) || today != lastMaintenance)
                        {
                            MaintainFiles(path, work.Configuration);
                            appliedPolicy = work.Configuration;
                            lastMaintenance = today;
                        }
                        await stream.WriteAsync(bytes).ConfigureAwait(false);
                        if (++unflushed >= 128)
                        {
                            await stream.FlushAsync().ConfigureAwait(false);
                            unflushed = 0;
                        }
                        ClearFileError();
                    }
                    catch (Exception exception) when (IsLoggingFailure(exception))
                    {
                        Interlocked.Increment(ref _failedEntries);
                        Interlocked.Increment(ref _droppedEntries);
                        SetFileError($"日志文件写入失败，本条未保存：{exception.Message}");
                        if (stream is not null)
                        {
                            try { await stream.DisposeAsync().ConfigureAwait(false); } catch (Exception closeError) when (IsLoggingFailure(closeError)) { }
                            stream = null;
                        }
                    }
                    finally { Interlocked.Decrement(ref _pendingEntries); Interlocked.Add(ref _pendingBytes, -work.EstimatedBytes); }
                }
                if (stream is not null && unflushed > 0)
                {
                    try { await stream.FlushAsync().ConfigureAwait(false); unflushed = 0; }
                    catch (Exception exception) when (IsLoggingFailure(exception)) { SetFileError($"日志刷新失败：{exception.Message}"); }
                }
            }
        }
        finally
        {
            if (stream is not null)
            {
                try { await stream.FlushAsync().ConfigureAwait(false); stream.Flush(flushToDisk: true); }
                catch (Exception exception) when (IsLoggingFailure(exception)) { SetFileError($"退出时日志刷新失败：{exception.Message}"); }
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (IsLoggingFailure(exception)) { SetFileError($"关闭日志文件失败：{exception.Message}"); }
            }
        }
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
            : JsonSerializer.SerializeToUtf8Bytes(new { timestamp = entry.Timestamp, level = entry.Level, kind = "sample", sample }, LineOptions);
        var line = new byte[content.Length + 1];
        content.CopyTo(line, 0); line[^1] = (byte)'\n';
        return line;
    }

    private void MaintainFiles(string activePath, GatewayLoggingConfiguration configuration)
    {
        var cutoff = DateTime.UtcNow.AddDays(-configuration.RetainedDays);
        var files = Directory.EnumerateFiles(_directory!, "gateway-*.log").Select(path => new FileInfo(path)).ToArray();
        foreach (var file in files.Where(file => file.FullName != activePath && file.LastWriteTimeUtc < cutoff)) file.Delete();
        var retained = files.Where(file => file.FullName != activePath && file.Exists)
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
        GatewayLoggingConfiguration Configuration, TaskCompletionSource? Barrier, long EstimatedBytes);
}
