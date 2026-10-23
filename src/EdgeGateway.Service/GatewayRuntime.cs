using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>
/// One sequential worker per device; acquisition, history storage and northbound publication are independent.
/// This instance owns driver sessions, but the caller retains ownership of the store and publisher.
/// </summary>
public sealed partial class GatewayRuntime : IAsyncDisposable
{
    private readonly IDriverRegistry _registry;
    private readonly IGatewayStore _store;
    private readonly ITelemetryPublisher _publisher;
    private readonly IGatewayLog _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TelemetrySample> _latest = new();
    private readonly ConcurrentDictionary<Guid, DeviceRuntimeStatus> _statuses = new();
    private readonly ConcurrentDictionary<Guid, DeviceSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _alarmStates = new();
    private volatile GatewayConfiguration _configuration;
    private CancellationTokenSource? _runCancellation;
    private Channel<IReadOnlyList<TelemetrySample>>? _historyChannel;
    private Task[] _sharedWorkers = [];
    private Task? _historyTask;
    private volatile bool _running;
    private bool _disposed;

    public GatewayRuntime(GatewayConfiguration configuration, IDriverRegistry registry,
        IGatewayStore store, ITelemetryPublisher publisher, IGatewayLog log)
    {
        _configuration = Clone(configuration);
        _registry = registry;
        _store = store;
        _publisher = publisher;
        _log = log;
        ResetStatuses();
    }

    public bool IsRunning => _running;
    public bool IsMqttConnected => _publisher.IsConnected;
    public bool IsDeviceRunning(Guid deviceId) => _sessions.TryGetValue(deviceId, out var session) && session.IsActive;

    public void Configure(GatewayConfiguration configuration)
    {
        if (!_lifecycle.Wait(0)) throw new InvalidOperationException("正在启动或停止网关，请稍后修改配置。");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) throw new InvalidOperationException("请先停止采集再应用配置。");
            var errors = ConfigurationValidator.Validate(configuration, _registry);
            if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(configuration));
            _configuration = Clone(configuration);
            _channels.Clear();
            _latest.Clear();
            ResetStatuses();
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateConfiguration();
            var devices = _configuration.Devices.Where(d => d.Enabled).ToArray();
            if (devices.Length == 0) return;
            await StartSharedServicesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var device in devices) StartDeviceCore(device);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StartDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var device = FindDevice(deviceId);
            if (!device.Enabled) throw new InvalidOperationException("设备未启用，请先编辑设备并启用。");
            if (IsDeviceRunning(deviceId)) return;
            ValidateConfiguration();
            await StartSharedServicesAsync(cancellationToken).ConfigureAwait(false);
            StartDeviceCore(device);
        }
        finally { _lifecycle.Release(); }
    }

    private DeviceConfiguration FindDevice(Guid deviceId) =>
        _configuration.Devices.FirstOrDefault(d => d.Id == deviceId) ?? throw new ArgumentException("设备不存在。", nameof(deviceId));

    private void ValidateConfiguration()
    {
        var errors = ConfigurationValidator.Validate(_configuration, _registry);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private async Task StartSharedServicesAsync(CancellationToken cancellationToken)
    {
        if (_runCancellation is not null) return;
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _store.PruneHistoryAsync(_configuration.HistoryRetentionDays, _configuration.MaxHistoryRows, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // The caller's token cancels startup only. Sessions own their subsequent lifetime.
        _runCancellation = new CancellationTokenSource();
        _historyChannel = Channel.CreateBounded<IReadOnlyList<TelemetrySample>>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        var reader = _historyChannel.Reader;
        var token = _runCancellation.Token;
        _running = true;
        _historyTask = Task.Run(() => StoreHistoryAsync(reader));
        _sharedWorkers = _configuration.Mqtt.Enabled
            ? [Task.Run(() => QueueTelemetryAsync(token)), Task.Run(() => SendOutboxAsync(token)), Task.Run(() => RunPlatformSessionAsync(token))]
            : [];
    }

    // Called only while holding _lifecycle, after shared services have started.
    private void StartDeviceCore(DeviceConfiguration device)
    {
        if (_sessions.ContainsKey(device.Id)) return;
        foreach (var tag in device.Tags)
        {
            _latest.TryRemove(tag.Id, out _);
            _alarmStates.TryRemove(tag.Id, out _);
        }
        _deviceGenerations.AddOrUpdate(device.Id, 1, (_, generation) => generation + 1);
        SetDemotion(device.Id, null);
        var session = new DeviceSession(device, _runCancellation!.Token);
        _sessions[device.Id] = session;
        _statuses[device.Id] = new(device.Id, device.Name, DeviceState.Connecting,
            _statuses.GetValueOrDefault(device.Id)?.LastSuccess, 0, null);
        session.Worker = Task.Run(() => CollectDeviceAsync(session, session.Token));
        _log.Write(GatewayLogLevel.Information, $"设备 [{device.Name}] 采集已启动，当前运行 {_sessions.Count} 台设备。");
    }

    public async Task StopDeviceAsync(Guid deviceId)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FindDevice(deviceId);
            if (!_sessions.TryGetValue(deviceId, out var session)) return;
            session.RequestStop();
            await FinishDeviceStopAsync(session).ConfigureAwait(false);
            _sessions.TryRemove(deviceId, out _);
            if (_sessions.IsEmpty) await StopSharedServicesAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        if (_runCancellation is null) return;
        var sessions = _sessions.Values.ToArray();
        foreach (var session in sessions) session.RequestStop();
        _runCancellation.Cancel();
        // Cancel every device before waiting, so one slow driver does not delay cancellation of others.
        foreach (var session in sessions) await FinishDeviceStopAsync(session).ConfigureAwait(false);
        _sessions.Clear();
        await StopSharedServicesAsync().ConfigureAwait(false);
    }

    private async Task FinishDeviceStopAsync(DeviceSession session)
    {
        try { await session.Worker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        catch (Exception ex) { _log.Write(GatewayLogLevel.Error, $"停止设备 [{session.Configuration.Name}] 时发生错误：{ex.Message}"); }
        // A write may still be completing. Cancellation plus this gate prevents disposing
        // its driver or returning from Stop while a device operation is still in progress.
        await session.Gate.WaitAsync().ConfigureAwait(false);
        try { await DisposeDriverAsync(session).ConfigureAwait(false); }
        finally { session.Gate.Release(); }
        var device = session.Configuration;
        foreach (var pair in _latest.Where(pair => pair.Value.DeviceId == device.Id))
            _latest[pair.Key] = pair.Value with { Quality = SampleQuality.Uncertain, Error = "设备采集已停止" };
        _statuses[device.Id] = new(device.Id, device.Name, DeviceState.Stopped,
            _statuses.GetValueOrDefault(device.Id)?.LastSuccess, 0, null);
        session.DisposeCancellation();
        _log.Write(GatewayLogLevel.Information, $"设备 [{device.Name}] 采集已停止。");
    }

    private async Task StopSharedServicesAsync()
    {
        _runCancellation?.Cancel();
        try { await Task.WhenAll(_sharedWorkers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Write(GatewayLogLevel.Error, $"停止上报任务时发生错误：{ex.Message}"); }
        // Only the last device closes the channel. Accepted history from every device
        // is drained while stopping one of several devices leaves this consumer alive.
        _historyChannel?.Writer.TryComplete();
        if (_historyTask is not null) await _historyTask.ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _publisher.DisconnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.Write(GatewayLogLevel.Warning, $"断开 MQTT 时发生错误：{ex.Message}"); }
        _runCancellation?.Dispose();
        _runCancellation = null;
        _sharedWorkers = [];
        _historyTask = null;
        _historyChannel = null;
        _running = false;
        _log.Write(GatewayLogLevel.Information, "全部设备已停止，历史缓冲已处理，未确认的上报仍保留在补传队列。");
    }

    public IReadOnlyList<TelemetrySample> GetLatest() => _latest.Values.OrderBy(s => s.DeviceName).ThenBy(s => s.TagName).ToArray();
    public TelemetrySample? GetLatestSample(Guid tagId) => _latest.GetValueOrDefault(tagId);
    public IReadOnlyList<DeviceRuntimeStatus> GetDeviceStatuses() => _statuses.Values.OrderBy(s => s.DeviceName).ToArray();

    /// <summary>Compatibility submission-only API. Successful completion does not confirm readback; failures retain their exception types.</summary>
    public Task WriteAsync(Guid deviceId, Guid tagId, string engineeringValue, CancellationToken cancellationToken = default) =>
        SubmitWriteCompatibilityAsync(deviceId, tagId, engineeringValue, cancellationToken);

    private async Task CollectDeviceAsync(DeviceSession session, CancellationToken cancellationToken)
    {
        var device = session.Configuration;
        var planner = new DeviceScanPlanner(device);
        var clock = Stopwatch.StartNew();
        var failures = 0;
        DateTimeOffset? lastSuccess = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tags = planner.GetDue(clock.ElapsedMilliseconds).ToArray();
                if (tags.Length == 0 && device.Tags.Any(t => t.Enabled))
                {
                    await Task.Delay(planner.DelayUntilNext(clock.ElapsedMilliseconds), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var delay = device.PollIntervalMs;
                var failed = false;
                long operationStarted = 0;
                IReadOnlyList<TelemetrySample> samples = [];
                await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // A channel's wait time is scheduling, not a device communication timeout.
                    await using var channelLease = await EnterChannelAsync(device, cancellationToken).ConfigureAwait(false);
                    operationStarted = Stopwatch.GetTimestamp();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var legacy = _registry.GetFactory(device.DriverId).Descriptor.IsLegacy;
                    // Original drivers have per-operation synchronous timeouts; budget the serial per-tag cycle separately.
                    timeout.CancelAfter(legacy ? (int)Math.Min(int.MaxValue - 1L, (long)(device.TimeoutMs + 1000) * (tags.Length + 1) + 500) : device.TimeoutMs);
                    if (session.Driver is null)
                    {
                        _statuses[device.Id] = new(device.Id, device.Name, DeviceState.Connecting, lastSuccess, failures, null);
                        session.Driver = _registry.GetFactory(device.DriverId).Create(device);
                        await session.Driver.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    }
                    var reads = await session.Driver.ReadAsync(tags, timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    var byId = reads.GroupBy(r => r.TagId).ToDictionary(g => g.Key, g => g.First());
                    var now = DateTimeOffset.UtcNow;
                    samples = tags.Select(tag => BuildSample(device, tag,
                        byId.GetValueOrDefault(tag.Id) ?? new TagReadResult(tag.Id, null, SampleQuality.Bad, "驱动未返回此点位"), now)).ToArray();
                    // Conversion/expression errors are point-quality errors, not a lost connection.
                    var hasGoodSample = tags.Any(t => byId.GetValueOrDefault(t.Id)?.Quality == SampleQuality.Good);
                    var readError = tags.Any(t => byId.GetValueOrDefault(t.Id)?.Quality != SampleQuality.Good);
                    RecordDeviceOperation(device.Id, false, !readError, Stopwatch.GetElapsedTime(operationStarted),
                        readError ? reads.FirstOrDefault(r => r.Quality != SampleQuality.Good)?.Error ?? "部分点位未返回有效值" : null);
                    if (hasGoodSample || tags.Length == 0)
                    {
                        if (failures > 0) _log.Write(GatewayLogLevel.Information, $"设备 [{device.Name}] 连接已恢复。");
                        failures = 0;
                        SetDemotion(device.Id, null);
                        lastSuccess = now;
                        _statuses[device.Id] = new(device.Id, device.Name, DeviceState.Running, lastSuccess, 0,
                            samples.Any(s => s.Quality != SampleQuality.Good) ? "部分点位采集失败，请查看点位质量。" : null);
                    }
                    else
                    {
                        failures++;
                        var error = samples.FirstOrDefault()?.Error ?? "没有有效采样值";
                        _statuses[device.Id] = new(device.Id, device.Name, DeviceState.Reconnecting, lastSuccess, failures, error);
                        failed = true;
                        delay = ReconnectDelay(device, failures);
                        await DisposeDriverAsync(session).ConfigureAwait(false);
                        _log.Write(GatewayLogLevel.Warning, $"设备 [{device.Name}] 无有效采样，将重连：{error}");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failures++;
                    failed = true;
                    var error = ex is OperationCanceledException ? $"采集超过 {device.TimeoutMs} ms" : ex.Message;
                    if (operationStarted != 0) RecordDeviceOperation(device.Id, false, false, Stopwatch.GetElapsedTime(operationStarted), error);
                    samples = tags.Select(tag => BuildSample(device, tag, new TagReadResult(tag.Id, null, SampleQuality.Bad, error), DateTimeOffset.UtcNow)).ToArray();
                    _statuses[device.Id] = new(device.Id, device.Name, DeviceState.Reconnecting, lastSuccess, failures, error);
                    _log.Write(GatewayLogLevel.Warning, $"设备 [{device.Name}] 采集失败，第 {failures} 次重连：{error}");
                    await DisposeDriverAsync(session).ConfigureAwait(false);
                    delay = ReconnectDelay(device, failures);
                }
                finally
                {
                    try
                    {
                        // Publish this read while still owning the device gate. Otherwise a
                        // newer write can mark Uncertain and then be overwritten by this old read.
                        foreach (var sample in samples)
                        {
                            _latest[sample.TagId] = sample;
                            UpdateAlarm(tags.First(t => t.Id == sample.TagId), sample);
                        }
                    }
                    finally { session.Gate.Release(); }
                }
                planner.Complete(tags, clock.ElapsedMilliseconds);
                if (!failed) delay = planner.DelayUntilNext(clock.ElapsedMilliseconds);
                if (samples.Count > 0) await _historyChannel!.Writer.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _statuses[device.Id] = new(device.Id, device.Name, DeviceState.Faulted, lastSuccess, failures, ex.Message);
            _log.Write(GatewayLogLevel.Error, $"设备 [{device.Name}] 工作循环异常退出：{ex.Message}");
        }
        finally
        {
            await session.Gate.WaitAsync().ConfigureAwait(false);
            try { await DisposeDriverAsync(session).ConfigureAwait(false); }
            finally { session.Gate.Release(); }
        }
    }

    private async Task StoreHistoryAsync(ChannelReader<IReadOnlyList<TelemetrySample>> reader)
    {
        var nextPrune = DateTimeOffset.UtcNow.AddMinutes(1);
        await foreach (var batch in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _store.AppendSamplesAsync(batch).ConfigureAwait(false);
                if (DateTimeOffset.UtcNow >= nextPrune)
                {
                    await _store.PruneHistoryAsync(_configuration.HistoryRetentionDays, _configuration.MaxHistoryRows).ConfigureAwait(false);
                    nextPrune = DateTimeOffset.UtcNow.AddMinutes(1);
                }
            }
            catch (Exception ex)
            {
                // Keep acquisition alive, but explicitly report that this batch was NOT persisted.
                _log.Write(GatewayLogLevel.Error, $"历史写入失败，本批 {batch.Count} 条未保存，请检查磁盘：{ex.Message}");
            }
        }
    }

    private void UpdateAlarm(TagConfiguration tag, TelemetrySample sample)
    {
        if (sample.Quality != SampleQuality.Good || sample.NumericValue is not double value) return;
        var state = tag.HighLimit is double high && value > high ? "高限报警" :
            tag.LowLimit is double low && value < low ? "低限报警" : "正常";
        var previous = _alarmStates.GetValueOrDefault(tag.Id, "正常");
        _alarmStates[tag.Id] = state;
        if (previous == state) return;
        _log.Write(state == "正常" ? GatewayLogLevel.Information : GatewayLogLevel.Warning,
            $"{state}：设备 [{sample.DeviceName}] 点位 [{sample.TagName}] 当前值 {value.ToString("G", CultureInfo.InvariantCulture)} {sample.Unit}");
    }

    private static TelemetrySample BuildSample(DeviceConfiguration device, TagConfiguration tag, TagReadResult result, DateTimeOffset timestamp)
    {
        object? raw = result.Value;
        object? value = raw;
        var quality = result.Quality;
        var error = result.Error;
        if (quality != SampleQuality.Bad)
        {
            try
            {
                if (raw is null) throw new InvalidDataException("驱动返回空值。");
                value = SampleValueConversion.ConvertReadValue(tag, raw);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidCastException or OverflowException)
            {
                raw = null;
                value = null;
                quality = SampleQuality.Bad;
                error = ex.Message;
            }
        }
        else
        {
            // Failed reads must never continue publishing an old value as if it were fresh.
            raw = null;
            value = null;
        }
        return new TelemetrySample(device.Id, device.Name, tag.Id, tag.Name, tag.Address, raw, value, quality, timestamp, tag.Unit, error);
    }

    private static object ConvertWriteValue(TagConfiguration tag, string text)
    {
        if (TagDataTypes.IsString(tag.DataType)) return text;
        if (tag.DataType is TagDataType.Int64 or TagDataType.UInt64 or TagDataType.TimeStampMs or TagDataType.TimeStampS)
            return SampleValueConversion.ConvertWideIntegerWrite(tag, text);
        if (tag.DataType == TagDataType.Boolean)
        {
            if (bool.TryParse(text, out var boolean)) return boolean;
            return text.Trim() switch { "1" => true, "0" => false, _ => throw new ArgumentException("布尔点位只接受 true、false、1 或 0。") };
        }
        if (tag.DataType == TagDataType.Bit && bool.TryParse(text, out var bit)) text = bit ? "1" : "0";
        if (tag.Scale == 0 || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var engineering) || !double.IsFinite(engineering))
            throw new ArgumentException("请输入有效的有限数值，使用小数点作为分隔符；写入倍率不能为 0。");
        var raw = (engineering - tag.Offset) / tag.Scale;
        if (!double.IsFinite(raw)) throw new ArgumentException("反算后的原始值超出范围。");
        if (tag.DataType is TagDataType.Bit or TagDataType.Int8 or TagDataType.UInt8 or TagDataType.Int16 or TagDataType.UInt16 or TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Bcd16 or TagDataType.Bcd32)
        {
            // Permit only a few representable floating-point steps from an integer, e.g. 0.3 / 0.1.
            // A fixed decimal epsilon would incorrectly accept genuine fractions near zero or large magnitudes.
            var rounded = Math.Round(raw);
            var ulp = Math.Max(Math.Abs(Math.BitIncrement(raw) - raw), Math.Abs(raw - Math.BitDecrement(raw)));
            if (Math.Abs(raw - rounded) > 4 * ulp)
                throw new ArgumentException("工程值反算后不是整数，无法写入整数寄存器。");
            raw = rounded;
        }
        return tag.DataType switch
        {
            TagDataType.Bit when raw is 0 or 1 => (object)(int)raw,
            TagDataType.Int8 when raw >= sbyte.MinValue && raw <= sbyte.MaxValue => (object)(sbyte)raw,
            TagDataType.UInt8 when raw >= byte.MinValue && raw <= byte.MaxValue => (object)(byte)raw,
            TagDataType.Bcd16 when raw >= 0 && raw <= 9999 => (object)(ushort)raw,
            TagDataType.Bcd32 when raw >= 0 && raw <= 99999999 => (object)(uint)raw,
            TagDataType.Int16 when raw >= short.MinValue && raw <= short.MaxValue => (object)(short)raw,
            TagDataType.UInt16 when raw >= ushort.MinValue && raw <= ushort.MaxValue => (object)(ushort)raw,
            TagDataType.Int32 when raw >= int.MinValue && raw <= int.MaxValue => (object)(int)raw,
            TagDataType.UInt32 when raw >= uint.MinValue && raw <= uint.MaxValue => (object)(uint)raw,
            TagDataType.Float32 when Math.Abs(raw) <= float.MaxValue => (object)(float)raw,
            TagDataType.Float64 => (object)raw,
            _ => throw new ArgumentOutOfRangeException(nameof(text), "反算后的原始值超出目标数据类型范围。")
        };
    }

    private async Task DisposeDriverAsync(DeviceSession session)
    {
        if (session.Driver is null) return;
        try { await session.Driver.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Write(GatewayLogLevel.Warning, $"关闭设备 [{session.Configuration.Name}] 时发生错误：{ex.Message}"); }
        finally { session.Driver = null; }
    }

    private void ResetStatuses()
    {
        _statuses.Clear();
        foreach (var device in _configuration.Devices ?? [])
            if (device is not null) _statuses[device.Id] = new(device.Id, device.Name, DeviceState.Stopped, null, 0, null);
    }

    private static GatewayConfiguration Clone(GatewayConfiguration configuration) =>
        JsonSerializer.Deserialize<GatewayConfiguration>(JsonSerializer.Serialize(configuration))!;
    private static int Backoff(int failures) => (int)Math.Min(30000, 1000 * Math.Pow(2, Math.Min(failures - 1, 5)));

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await StopCoreAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally { _lifecycle.Release(); }
    }

    private sealed class DeviceSession
    {
        private readonly CancellationTokenSource _cancellation;
        private int _stopRequested;
        public DeviceConfiguration Configuration { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationToken Token { get; }
        public Task Worker { get; set; } = Task.CompletedTask;
        public IDeviceDriver? Driver { get; set; }
        public bool IsActive => Volatile.Read(ref _stopRequested) == 0;

        public DeviceSession(DeviceConfiguration configuration, CancellationToken runtimeToken)
        {
            Configuration = configuration;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(runtimeToken);
            Token = _cancellation.Token;
        }

        public void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0) _cancellation.Cancel();
        }

        public void DisposeCancellation() => _cancellation.Dispose();
    }
}
