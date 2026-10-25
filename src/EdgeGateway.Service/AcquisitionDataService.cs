using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>
/// Acquisition produces log entries and optional external rows without waiting for database I/O.
/// Each write group owns a worker; all groups on a connection share row and byte budgets.
/// The queues are deliberately volatile. No samples are stored in local SQLite.
/// </summary>
public sealed class AcquisitionDataService(IExternalDatabaseConnector? connector, IGatewayLog log) : IAcquisitionDataSink
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private GatewayConfiguration _configuration = new();
    private Dictionary<Guid, Guid> _enabledTags = [];
    private readonly Dictionary<Guid, DateTimeOffset> _activeDevices = [];
    private readonly Dictionary<Guid, TelemetrySample> _latest = [];
    private readonly Dictionary<Guid, long> _versions = [];
    private readonly Dictionary<Guid, long> _latestSizes = [];
    private HashSet<Guid> _mappedTags = [];
    private long _latestBytes;
    private const long MaxSnapshotBytes = 64L * 1024 * 1024;
    private Dictionary<Guid, ConnectionState> _connections = [];
    private GroupWorker[] _workers = [];
    private CancellationTokenSource? _shutdown;
    private CancellationTokenSource? _tickerCancellation;
    private Task _ticker = Task.CompletedTask;
    private long _sequence;
    private bool _running;
    private bool _disposed;

    public void UpdateConfiguration(GatewayConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var next = GatewayJson.Clone(configuration);
        var errors = DataRecordingConfigurationValidator.Validate(next);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running && (JsonSerializer.Serialize(next.Databases, GatewayJson.Options) != JsonSerializer.Serialize(_configuration.Databases, GatewayJson.Options)
                || JsonSerializer.Serialize(next.DatabaseWriteGroups, GatewayJson.Options) != JsonSerializer.Serialize(_configuration.DatabaseWriteGroups, GatewayJson.Options)))
                throw new InvalidOperationException("请先停止全部设备，再修改数据库设置。");
            _configuration = next;
            _enabledTags = next.Devices.Where(device => device.Enabled)
                .SelectMany(device => device.Tags.Where(tag => tag.Enabled).Select(tag => (tag.Id, DeviceId: device.Id)))
                .ToDictionary(pair => pair.Id, pair => pair.DeviceId);
            foreach (var id in _latest.Keys.Where(id => !_enabledTags.ContainsKey(id)).ToArray()) RemoveCached(id);
        }
        if (log is IConfigurableGatewayLog configurable) configurable.ApplyConfiguration(next.Logging);
    }

    public void SetDeviceActive(Guid deviceId, bool active)
    {
        lock (_sync)
        {
            if (active) _activeDevices[deviceId] = DateTimeOffset.UtcNow; else _activeDevices.Remove(deviceId);
            // Restarts invalidate the previous generation even if the identifiers are unchanged.
            foreach (var id in _latest.Where(pair => pair.Value.DeviceId == deviceId).Select(pair => pair.Key).ToArray())
                RemoveCached(id);
        }
    }

    private void RemoveCached(Guid id)
    {
        _latest.Remove(id); _versions.Remove(id);
        if (_latestSizes.Remove(id, out var bytes)) _latestBytes -= bytes;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_running) return;
                if (_workers.Any(worker => !worker.Task.IsCompleted)) throw new InvalidOperationException("上一次数据库操作尚未退出，请等待其超时后再启动。");
                if (connector is null && _configuration.DatabaseWriteGroups.Any(group => group.Enabled && _configuration.Databases.Any(connection => connection.Id == group.ConnectionId && connection.Enabled)))
                    throw new InvalidOperationException("宿主未提供外部数据库连接器。");
                _shutdown?.Dispose();
                _tickerCancellation?.Dispose();
                _shutdown = new CancellationTokenSource();
                _tickerCancellation = new CancellationTokenSource();
                _latest.Clear(); _versions.Clear(); _latestSizes.Clear(); _latestBytes = 0; _activeDevices.Clear();
                _connections = _configuration.Databases.ToDictionary(connection => connection.Id, connection => new ConnectionState(connection, log));
                _workers = _configuration.DatabaseWriteGroups.Where(group => group.Enabled && _connections[group.ConnectionId].Configuration.Enabled)
                    .Select(group => new GroupWorker(group, _connections[group.ConnectionId], connector!, _shutdown.Token)).ToArray();
                _mappedTags = _workers.SelectMany(worker => worker.RequiredTags).ToHashSet();
                _running = true;
                foreach (var worker in _workers) worker.Start();
                _ticker = Task.Run(() => TickAsync(_tickerCancellation.Token));
            }
        }
        finally { _lifecycle.Release(); }
    }

    public Task AppendAsync(IReadOnlyList<TelemetrySample> samples, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_running || _disposed) return Task.CompletedTask;
            foreach (var sample in samples)
            {
                if (log is IConfigurableGatewayLog configurable) configurable.WriteSample(sample);
                else if (_configuration.Logging.LogAcquisitionSamples && _configuration.Logging.MinimumLevel <= GatewayLogLevel.Information)
                    log.Write(GatewayLogLevel.Information, "采集 " + JsonSerializer.Serialize(sample));
                if (!_enabledTags.TryGetValue(sample.TagId, out var deviceId) || deviceId != sample.DeviceId) continue;
                if (_workers.Length == 0) continue;
                if (!SamplePayloadBudget.TryEstimate(sample, out var sampleBytes))
                {
                    RemoveCached(sample.TagId);
                    foreach (var worker in _workers.Where(worker => worker.Group.Mode == DatabaseStorageMode.PointRows
                        ? worker.Accepts(sample.TagId) : worker.RequiredTags.Contains(sample.TagId)))
                        worker.State.Drop(worker.Group.Id, 1, "单条采样超出 1 M 或结构复杂度限制，未进入数据库队列或字段映射缓存。");
                    continue;
                }
                if (_mappedTags.Contains(sample.TagId) && _activeDevices.TryGetValue(deviceId, out var activeSince) && sample.Timestamp >= activeSince)
                {
                    RemoveCached(sample.TagId);
                    if (_latestBytes + sampleBytes <= MaxSnapshotBytes)
                    {
                        _latest[sample.TagId] = sample;
                        _versions[sample.TagId] = ++_sequence;
                        _latestSizes[sample.TagId] = sampleBytes; _latestBytes += sampleBytes;
                    }
                    else foreach (var worker in _workers.Where(worker => worker.RequiredTags.Contains(sample.TagId)))
                        worker.State.Drop(worker.Group.Id, 1, "自定义字段的最新采样缓存已达 64 M 配额，未使用旧值生成新行。");
                }
                DatabaseWriteRow? row = null;
                foreach (var worker in _workers.Where(worker => worker.Group.Mode == DatabaseStorageMode.PointRows && worker.Accepts(sample.TagId)))
                {
                    try { worker.Enqueue(row ??= DatabaseRowMapper.PointRow(sample)); }
                    catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
                    { worker.State.Drop(worker.Group.Id, 1, $"写入组 [{worker.Group.Name}] 无法序列化采集值：{exception.Message}"); }
                }
            }
            EmitMappedRows(DateTimeOffset.UtcNow);
        }
        return Task.CompletedTask;
    }

    private async Task TickAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                lock (_sync) { if (_running) EmitMappedRows(DateTimeOffset.UtcNow); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // Called under _sync. Complete rows only: no old value is silently substituted for a missing/bad sample.
    private void EmitMappedRows(DateTimeOffset now)
    {
        var ticks = Stopwatch.GetTimestamp();
        foreach (var worker in _workers.Where(worker => worker.Group.Mode == DatabaseStorageMode.ColumnMapping))
        {
            if (ticks < worker.NextSnapshotTicks) continue;
            worker.NextSnapshotTicks = ticks + (long)(worker.Group.WriteIntervalMs / 1000d * Stopwatch.Frequency);
            var newestVersion = worker.RequiredTags.Select(id => _versions.GetValueOrDefault(id)).DefaultIfEmpty(0).Max();
            if (newestVersion <= worker.LastSnapshotVersion) continue;
            var missing = worker.RequiredTags.FirstOrDefault(id => !_enabledTags.TryGetValue(id, out var deviceId) || !_activeDevices.ContainsKey(deviceId)
                || !_latest.TryGetValue(id, out var sample) || now - sample.Timestamp > TimeSpan.FromMilliseconds(worker.Group.MaxSampleAgeMs)
                || sample.Timestamp - now > TimeSpan.FromSeconds(5) || (worker.Group.RequireGoodQuality && sample.Quality != SampleQuality.Good));
            if (missing != Guid.Empty)
            {
                worker.State.Skip(worker.Group.Id, $"写入组 [{worker.Group.Name}] 等待齐全、活跃且符合质量/时效要求的点位；未生成不完整行。");
                continue;
            }
            try
            {
                var row = DatabaseRowMapper.MappedRow(worker.Group, _latest, now);
                worker.Enqueue(row);
                worker.LastSnapshotVersion = newestVersion;
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or NotSupportedException)
            {
                worker.State.Skip(worker.Group.Id, $"写入组 [{worker.Group.Name}] 字段映射失败：{exception.Message}");
                worker.LastSnapshotVersion = newestVersion;
            }
        }
    }

    public IReadOnlyList<ExternalDatabaseStatus> GetStatuses()
    {
        lock (_sync)
            return _configuration.Databases.Select(connection => _connections.TryGetValue(connection.Id, out var state)
                ? state.Snapshot(connection.Enabled) : new ExternalDatabaseStatus(connection.Id, connection.Name, connection.Enabled, 0, 0, 0, 0, 0, null, null, false)).ToArray();
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Task[] tasks;
            lock (_sync)
            {
                if (!_running) return;
                _running = false;
                _tickerCancellation!.Cancel();
                _activeDevices.Clear();
                foreach (var worker in _workers) worker.Complete();
                tasks = _workers.Select(worker => worker.Task).ToArray();
            }
            await _ticker.ConfigureAwait(false);
            var all = Task.WhenAll(tasks);
            try { await all.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _shutdown!.Cancel();
                try { await all.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException) { log.Write(GatewayLogLevel.Error, "数据库操作未及时响应停止；已隔离该操作，完成前禁止重启采集。尚未写入的内存记录不保证保留。"); }
            }
            foreach (var state in _connections.Values) state.SetStopped();
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _disposed = true;
            _tickerCancellation?.Dispose();
            if (_workers.All(worker => worker.Task.IsCompleted)) _shutdown?.Dispose();
        }
    }

    private sealed class ConnectionState(DatabaseConnectionConfiguration configuration, IGatewayLog logger)
    {
        private readonly object _gate = new();
        private long _queued, _bytes, _written, _dropped, _failed, _skipped;
        private DateTimeOffset? _lastSuccess, _lastFailure;
        private long _peak, _retries, _timeouts;
        private int _inFlight;
        private double _lastWriteMilliseconds;
        private readonly Dictionary<Guid, string> _errors = [];
        private bool _connected;
        private bool _stopped;
        private long _nextWarning;
        public DatabaseConnectionConfiguration Configuration { get; } = configuration;
        public bool Reserve(long bytes)
        {
            lock (_gate)
            {
                if (_queued >= Configuration.QueueCapacityRows || _bytes + bytes > Configuration.QueueCapacityMb * 1024L * 1024) return false;
                _queued++; _bytes += bytes; _peak = Math.Max(_peak, _queued); return true;
            }
        }
        public void Release(int rows, long bytes) { lock (_gate) { _queued -= rows; _bytes -= bytes; } }
        public void Success(Guid groupId, int rows) { lock (_gate) { _written += rows; _lastSuccess = DateTimeOffset.UtcNow; _errors.Remove(groupId); _connected = !_stopped && _errors.Count == 0; } }
        public void Failure(Guid groupId, string error) { lock (_gate) { _failed++; _lastFailure = DateTimeOffset.UtcNow; _connected = false; _errors[groupId] = error; Warn(error); } }
        public void Drop(Guid groupId, int rows, string error) { lock (_gate) { _dropped += rows; _errors[groupId] = error; _connected = false; Warn(error); } }
        public void Skip(Guid groupId, string error) { lock (_gate) { _skipped++; _errors[groupId] = error; _connected = false; Warn(error); } }
        private void Warn(string error)
        {
            var now = Stopwatch.GetTimestamp();
            if (now < _nextWarning) return;
            _nextWarning = now + 10 * Stopwatch.Frequency;
            logger.Write(GatewayLogLevel.Warning, $"数据库 [{Configuration.Name}]：{error}");
        }
        public void AttemptStarted(bool retry) { lock (_gate) { _inFlight++; if (retry) _retries++; } }
        public void AttemptFinished(long started) { lock (_gate) { _inFlight--; _lastWriteMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; } }
        public void TimedOut(Guid groupId, bool unresponsive)
        {
            lock (_gate)
            {
                _timeouts++; _lastFailure = DateTimeOffset.UtcNow; _connected = false;
                _errors[groupId] = unresponsive
                    ? "数据库调用未响应截止；正在隔离并等待原调用退出，不会并发重试。"
                    : "数据库调用超过时间限制，已退出本次调用。";
                Warn(_errors[groupId]);
            }
        }
        public void SetStopped() { lock (_gate) { _stopped = true; _connected = false; } }
        public ExternalDatabaseStatus Snapshot(bool enabled)
        {
            lock (_gate) return new(Configuration.Id, Configuration.Name, enabled, _queued, _written, _dropped, _failed, _skipped, _lastSuccess, _errors.Count == 0 ? null : string.Join("；", _errors.Values.Distinct().Take(4)), _connected)
            {
                QueuedBytes = _bytes, PeakQueuedRows = _peak, RetriedBatches = _retries, TimedOutOperations = _timeouts,
                InFlightBatches = _inFlight, LastWriteMilliseconds = _lastWriteMilliseconds, LastFailure = _lastFailure,
                IsBackpressured = _queued >= Configuration.QueueCapacityRows * 0.8 || _bytes >= Configuration.QueueCapacityMb * 1024L * 1024 * 0.8
            };
        }
    }

    private sealed class GroupWorker
    {
        private readonly IExternalDatabaseConnector _connector;
        private readonly CancellationToken _token;
        private readonly Channel<QueuedRow> _queue;
        private readonly HashSet<Guid> _selected;
        private long _nextBatchTicks;
        public DatabaseWriteGroupConfiguration Group { get; }
        public ConnectionState State { get; }
        public Guid[] RequiredTags { get; }
        public long NextSnapshotTicks;
        public long LastSnapshotVersion;
        public Task Task { get; private set; } = Task.CompletedTask;
        public GroupWorker(DatabaseWriteGroupConfiguration group, ConnectionState state, IExternalDatabaseConnector databaseConnector, CancellationToken token)
        {
            Group = group; State = state; _connector = databaseConnector; _token = token;
            _selected = group.TagIds.ToHashSet();
            RequiredTags = group.Mode == DatabaseStorageMode.ColumnMapping
                ? group.Columns.Where(column => column.Source is not (DatabaseValueSource.Constant or DatabaseValueSource.RecordTime or DatabaseValueSource.EventId))
                    .Select(column => column.TagId!.Value).Distinct().ToArray() : [];
            _queue = Channel.CreateBounded<QueuedRow>(new BoundedChannelOptions(state.Configuration.QueueCapacityRows)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        }
        public bool Accepts(Guid tagId) => _selected.Count == 0 || _selected.Contains(tagId);
        public void Start() => Task = System.Threading.Tasks.Task.Run(RunAsync);
        public void Complete() => _queue.Writer.TryComplete();
        public void Enqueue(DatabaseWriteRow row)
        {
            var bytes = DatabaseRowMapper.EstimateBytes(row);
            if (bytes > 1024 * 1024) { State.Drop(Group.Id, 1, $"写入组 [{Group.Name}] 单行超过 1 M，已拒绝该行。"); return; }
            if (!State.Reserve(bytes)) { State.Drop(Group.Id, 1, "写入队列已达行数或内存配额，拒绝新记录；已有记录继续重试。"); return; }
            if (!_queue.Writer.TryWrite(new(row, bytes))) { State.Release(1, bytes); State.Drop(Group.Id, 1, "写入队列已关闭，记录未入库。"); }
        }
        private async Task RunAsync()
        {
            List<QueuedRow> batch = [];
            try
            {
                while (await _queue.Reader.WaitToReadAsync(_token).ConfigureAwait(false))
                {
                    // A permanently invalid table/credential must not hammer the database
                    // once per newly arriving batch. Keep only this group's worker in cooldown.
                    var cooldown = TimeSpan.FromSeconds((_nextBatchTicks - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                    if (cooldown > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(cooldown, _token).ConfigureAwait(false);
                    batch.Clear();
                    if (!_queue.Reader.TryRead(out var first)) continue;
                    batch.Add(first);
                    var bytes = first.Bytes;
                    var deadline = Stopwatch.GetTimestamp() + (long)(State.Configuration.FlushIntervalMs / 1000d * Stopwatch.Frequency);
                    while (batch.Count < State.Configuration.BatchSize && bytes < 8 * 1024 * 1024)
                    {
                        while (batch.Count < State.Configuration.BatchSize && bytes < 8 * 1024 * 1024 && _queue.Reader.TryRead(out var next)) { batch.Add(next); bytes += next.Bytes; }
                        var remaining = TimeSpan.FromSeconds((deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                        if (batch.Count >= State.Configuration.BatchSize || remaining <= TimeSpan.Zero) break;
                        using var fill = CancellationTokenSource.CreateLinkedTokenSource(_token);
                        fill.CancelAfter(remaining);
                        try { if (!await _queue.Reader.WaitToReadAsync(fill.Token).ConfigureAwait(false)) break; }
                        catch (OperationCanceledException) when (!_token.IsCancellationRequested) { break; }
                    }
                    await WriteBatchAsync(batch).ConfigureAwait(false);
                    State.Release(batch.Count, bytes);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception exception) { State.Failure(Group.Id, $"写入组 [{Group.Name}] 工作任务异常：{SafeError(exception)}"); }
            finally
            {
                // Unexpected worker failure must also stop future producers from reserving
                // rows in a channel that no longer has a reader.
                _queue.Writer.TryComplete();
                if (batch.Count > 0) { State.Release(batch.Count, batch.Sum(row => row.Bytes)); State.Drop(Group.Id, batch.Count, "停止宽限期内未完成的批次已丢弃；采集数据队列不落本地 SQLite。"); }
                long dropped = 0;
                while (_queue.Reader.TryRead(out var row)) { State.Release(1, row.Bytes); dropped++; }
                if (dropped > 0) State.Drop(Group.Id, checked((int)dropped), "停止时仍未写入的队列记录已丢弃。");
            }
        }
        private async Task WriteBatchAsync(IReadOnlyList<QueuedRow> batch)
        {
            var rows = batch.Select(item => item.Row).ToArray();
            string? terminalReason = null;
            for (var attempt = 1; attempt <= State.Configuration.MaxRetryAttempts; attempt++)
            {
                _token.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
                timeout.CancelAfter(TimeSpan.FromSeconds(State.Configuration.ConnectionTimeoutSeconds + State.Configuration.CommandTimeoutSeconds + 2));
                var started = Stopwatch.GetTimestamp();
                var timeoutCounted = false;
                State.AttemptStarted(attempt > 1);
                try
                {
                    // The same event IDs and values survive every attempt. Standard tables deduplicate by event_id.
                    var operation = _connector.WriteRowsAsync(State.Configuration, Group, rows, timeout.Token);
                    try { await operation.WaitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!operation.IsCompleted)
                    {
                        // Cancellation is not proof that a provider/native call stopped. Retain the
                        // worker and its reservation until the actual operation ends; never overlap retries.
                        if (!_token.IsCancellationRequested)
                        {
                            State.TimedOut(Group.Id, unresponsive: true);
                            timeoutCounted = true;
                        }
                        await operation.ConfigureAwait(false);
                    }
                    _nextBatchTicks = 0;
                    State.Success(Group.Id, rows.Length); return;
                }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    if (!timeoutCounted && (timeout.IsCancellationRequested || exception is TimeoutException or OperationCanceledException))
                        State.TimedOut(Group.Id, unresponsive: false);
                    State.Failure(Group.Id, $"写入组 [{Group.Name}] 第 {attempt}/{State.Configuration.MaxRetryAttempts} 次写入失败：{SafeError(exception)}");
                    if (exception is DatabaseWriteException { IsTransient: false } or ArgumentException or InvalidDataException or NotSupportedException)
                    { terminalReason = "不可重试的配置、权限或数据错误：" + SafeError(exception); break; }
                    if (attempt == State.Configuration.MaxRetryAttempts) break;
                }
                finally { State.AttemptFinished(started); }
                // Independent workers use bounded jitter to avoid a simultaneous reconnect storm.
                var delay = Math.Min(30000, 250 * Math.Pow(2, attempt - 1));
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(delay * (0.8 + 0.2 * Random.Shared.NextDouble())), _token).ConfigureAwait(false);
            }
            _nextBatchTicks = Stopwatch.GetTimestamp() + (terminalReason is null ? 1 : 5) * Stopwatch.Frequency;
            State.Drop(Group.Id, rows.Length, $"写入组 [{Group.Name}] {terminalReason ?? "已达最大尝试次数"}，本批 {rows.Length} 行未获入库确认。");
        }
        private string SafeError(Exception exception)
        {
            var message = exception is OperationCanceledException ? "数据库操作超过配置的时间限制。" : exception.Message;
            var secret = State.Configuration.Password;
            if (!string.IsNullOrEmpty(secret)) message = message.Replace(secret, "***", StringComparison.Ordinal);
            return message.Replace('\r', ' ').Replace('\n', ' ');
        }
        private sealed record QueuedRow(DatabaseWriteRow Row, long Bytes);
    }
}
