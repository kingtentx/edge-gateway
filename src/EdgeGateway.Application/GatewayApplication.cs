using System.Security.Cryptography;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Application;

/// <summary>
/// Owns the runtime and application command ordering. The composition root retains ownership
/// of the injected stores and transport, and releases them after this application is disposed.
/// </summary>
public sealed partial class GatewayApplication : IGatewayApplication
{
    private readonly GatewayRuntime _runtime;
    private readonly IConfigurationStore _configurationStore;
    private readonly IGatewayStore _store;
    private readonly IConfigurationFiles _files;
    private readonly IExternalDatabaseConnector? _externalDatabases;
    private readonly IServerResourceMonitor? _serverResources;
    private readonly IDriverPluginManager? _driverPluginManager;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Dictionary<Guid, ConfirmationGrant> _confirmations = [];
    private volatile GatewayConfiguration _configuration;
    private volatile bool _disposed;
    private bool _configurationPersisted;
    private long _configurationVersion;

    public GatewayApplication(GatewayConfiguration configuration, IDriverRegistry drivers,
        IConfigurationStore configurationStore, IGatewayStore store, ITelemetryPublisher publisher,
        IGatewayLog log, IConfigurationFiles files, bool configurationPersisted = true,
        IExternalDatabaseConnector? externalDatabases = null, IAcquisitionDataSink? dataSink = null,
        IServerResourceMonitor? serverResources = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(configurationStore);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(files);
        _configuration = GatewayJson.Clone(configuration);
        Drivers = new DriverCatalog(drivers);
        var errors = Drivers.ValidateConfiguration(_configuration);
        if (errors.Count > 0) throw InvalidConfiguration(errors);
        _configurationStore = configurationStore;
        _store = store;
        _files = files;
        _externalDatabases = externalDatabases;
        _serverResources = serverResources;
        _driverPluginManager = drivers as IDriverPluginManager;
        Log = log;
        _configurationPersisted = configurationPersisted;
        _runtime = new GatewayRuntime(_configuration, drivers, store, publisher, log, dataSink ?? new AcquisitionDataService(externalDatabases, log));
    }

    public IDriverCatalog Drivers { get; }
    public IGatewayLog Log { get; }
    public bool IsRunning => _runtime.IsRunning;
    public bool IsMqttConnected => _runtime.IsMqttConnected;
    public bool IsDeviceRunning(Guid deviceId) => _runtime.IsDeviceRunning(deviceId);
    public GatewayConfiguration GetConfiguration() { ThrowIfDisposed(); return GatewayJson.Clone(_configuration); }
    public GatewayOverviewSummary GetOverviewSummary()
    {
        ThrowIfDisposed();
        var configuration = _configuration;
        return OverviewSummaryBuilder.Build(configuration, _runtime.GetDeviceStatuses(), _runtime.GetLatest(),
            configuration.Devices.Where(device => _runtime.IsDeviceRunning(device.Id)).Select(device => device.Id).ToHashSet());
    }
    public Task<ServerResourceSnapshot> GetServerResourcesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return _serverResources?.ReadAsync(cancellationToken) ?? Task.FromResult(new ServerResourceSnapshot(
            DateTimeOffset.UtcNow, Environment.MachineName, Environment.ProcessorCount, null, null, [], ["宿主未提供服务器监控。"]));
    }
    public IReadOnlyList<string> ValidateConfiguration(GatewayConfiguration candidate) => Drivers.ValidateConfiguration(candidate);

    public async Task<ConfigurationChangePreview> PreviewConfigurationAsync(GatewayConfiguration candidate, CancellationToken cancellationToken = default)
    {
        var next = Capture(candidate);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var errors = ValidateConfiguration(next);
            if (errors.Count > 0) throw InvalidConfiguration(errors);
            var previous = _configuration.Mqtt;
            var changed = MqttDestinationChanged(previous, next.Mqtt);
            var statistics = changed || previous.Platform != next.Mqtt.Platform
                ? await _store.GetStatisticsAsync(cancellationToken).ConfigureAwait(false) : new StoreStatistics(0);
            if (previous.Platform != next.Mqtt.Platform && statistics.OutboxCount > 0)
                throw new InvalidOperationException($"仍有 {statistics.OutboxCount} 条原平台报文待补传。请先使用原平台设置补传完成，再切换接入平台。");
            Guid? token = null;
            if (changed && previous.Platform == next.Mqtt.Platform && statistics.OutboxCount > 0)
            {
                foreach (var expired in _confirmations.Where(pair => DateTimeOffset.UtcNow - pair.Value.CreatedAt > TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray())
                    _confirmations.Remove(expired);
                if (_confirmations.Count >= 32) _confirmations.Remove(_confirmations.OrderBy(pair => pair.Value.CreatedAt).First().Key);
                token = Guid.NewGuid();
                _confirmations.Add(token.Value, new(_configurationVersion, Fingerprint(next), statistics.OutboxCount,
                    statistics.OldestOutboxAt, DateTimeOffset.UtcNow));
            }
            return new(token.HasValue, token, statistics.OutboxCount, GatewayJson.Clone(previous), GatewayJson.Clone(next.Mqtt));
        }
        finally { _commands.Release(); }
    }

    public Task<ConfigurationApplyResult> ApplyConfigurationAsync(GatewayConfiguration candidate,
        ConfigurationApplyOptions? options = null, CancellationToken cancellationToken = default) =>
        CommitAsync(Capture(candidate), options, requireStopped: false, cancellationToken);

    public Task<ConfigurationApplyResult> ReplaceConfigurationAsync(GatewayConfiguration candidate,
        ConfigurationApplyOptions? options = null, CancellationToken cancellationToken = default) =>
        CommitAsync(Capture(candidate), options, requireStopped: true, cancellationToken);

    private async Task<ConfigurationApplyResult> CommitAsync(GatewayConfiguration next, ConfigurationApplyOptions? options,
        bool requireStopped, CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (requireStopped && _runtime.IsRunning) throw new InvalidOperationException("请先停止全部设备，再替换整份配置。");
            return await ApplyCoreAsync(next, options, cancellationToken).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    private async Task<ConfigurationApplyResult> ApplyCoreAsync(GatewayConfiguration next, ConfigurationApplyOptions? options,
        CancellationToken cancellationToken)
    {
        var errors = ValidateConfiguration(next);
        if (errors.Count > 0) throw InvalidConfiguration(errors);
        await VerifyDestinationConfirmationAsync(next, options, cancellationToken).ConfigureAwait(false);
        // The runtime owns the save/session transaction. Do not publish the caller's draft
        // before it succeeds, nor observe caller cancellation after its durable commit.
        var result = await _runtime.ApplyConfigurationAsync(next, _configurationStore, cancellationToken).ConfigureAwait(false);
        _configuration = next;
        _configurationPersisted = true;
        _configurationVersion++;
        _confirmations.Clear();
        return result;
    }

    private async Task VerifyDestinationConfirmationAsync(GatewayConfiguration next, ConfigurationApplyOptions? options,
        CancellationToken cancellationToken)
    {
        if (_configuration.Mqtt.Platform != next.Mqtt.Platform || !MqttDestinationChanged(_configuration.Mqtt, next.Mqtt)) return;
        var statistics = await _store.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        if (statistics.OutboxCount == 0) return;
        if (options?.MqttDestinationConfirmation is not Guid token || !_confirmations.TryGetValue(token, out var grant)
            || grant.Version != _configurationVersion || grant.CandidateFingerprint != Fingerprint(next)
            || grant.OutboxCount != statistics.OutboxCount || grant.OldestOutboxAt != statistics.OldestOutboxAt
            || DateTimeOffset.UtcNow - grant.CreatedAt > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("仍有待发送消息，MQTT 目标变更需要重新预览并明确确认；配置或队列变化后原确认不再有效。");
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => StartCoreAsync(null, cancellationToken);
    public Task StartDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) => StartCoreAsync(deviceId, cancellationToken);

    private async Task StartCoreAsync(Guid? deviceId, CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (deviceId.HasValue)
            {
                var device = FindDevice(_configuration, deviceId.Value);
                if (!device.Enabled) throw new InvalidOperationException("设备未启用，请先在设备配置中启用。");
            }
            if (!_configurationPersisted) await ApplyCoreAsync(GatewayJson.Clone(_configuration), null, cancellationToken).ConfigureAwait(false);
            if (deviceId.HasValue) await _runtime.StartDeviceAsync(deviceId.Value, cancellationToken).ConfigureAwait(false);
            else await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    public Task StopDeviceAsync(Guid deviceId) => StopCoreAsync(deviceId);
    public Task StopAsync() => StopCoreAsync(null);
    private async Task StopCoreAsync(Guid? deviceId)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (deviceId.HasValue) await _runtime.StopDeviceAsync(deviceId.Value).ConfigureAwait(false);
            else await _runtime.StopAsync().ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    public IReadOnlyList<TelemetrySample> GetLatest() => _runtime.GetLatest();
    public TelemetrySample? GetLatestSample(Guid tagId) => _runtime.GetLatestSample(tagId);
    public IReadOnlyList<DeviceRuntimeStatus> GetDeviceStatuses() => _runtime.GetDeviceStatuses();
    public IReadOnlyList<DeviceCommunicationStatistics> GetCommunicationStatistics() => _runtime.GetCommunicationStatistics();
    public TelemetryPipelineStatus GetTelemetryStatus() => _runtime.GetTelemetryStatus();
    public RpcRuntimeStatus GetRpcStatus() => _runtime.GetRpcStatus();
    public Task<DeviceProbeResult> ProbeDeviceAsync(Guid deviceId, Guid? tagId = null, CancellationToken cancellationToken = default) =>
        _runtime.ProbeDeviceAsync(deviceId, tagId, cancellationToken);
    public Task<DeviceProbeResult> ProbeDeviceAsync(Guid deviceId, Guid? tagId, DeviceProbeOptions options, CancellationToken cancellationToken = default) =>
        _runtime.ProbeDeviceAsync(deviceId, tagId, options, cancellationToken);
    public Task<WriteCommandResult> WriteVerifiedAsync(Guid deviceId, Guid tagId, string engineeringValue,
        WriteVerificationOptions? options = null, CancellationToken cancellationToken = default) =>
        _runtime.WriteVerifiedAsync(deviceId, tagId, engineeringValue, options, cancellationToken);
    public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    { ThrowIfDisposed(); return _store.GetStatisticsAsync(cancellationToken); }
    public Task<GatewayConfiguration> ReadSavedConfigurationAsync(CancellationToken cancellationToken = default)
    { ThrowIfDisposed(); return _configurationStore.LoadAsync(cancellationToken); }
    public Task<GatewayConfiguration> ReadConfigurationFileAsync(string path, CancellationToken cancellationToken = default)
    { ThrowIfDisposed(); return _files.ReadAsync(path, cancellationToken); }
    public Task ExportConfigurationAsync(string path, CancellationToken cancellationToken = default) =>
        _files.ExportAsync(path, GetConfiguration(), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _confirmations.Clear();
            await _runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static InvalidDataException InvalidConfiguration(IReadOnlyList<string> errors) =>
        new("配置检查未通过：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    private static GatewayConfiguration Capture(GatewayConfiguration candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        try { return GatewayJson.Clone(candidate); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException("配置无法序列化，尚未保存或应用。", exception); }
    }
    private static DeviceConfiguration FindDevice(GatewayConfiguration configuration, Guid id) =>
        configuration.Devices.FirstOrDefault(device => device.Id == id) ?? throw new InvalidOperationException("设备不存在。");
    private static string Fingerprint(GatewayConfiguration candidate) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(candidate, GatewayJson.Options)));
    private static bool MqttDestinationChanged(MqttConfiguration previous, MqttConfiguration next) =>
        !string.Equals(previous.Host, next.Host, StringComparison.OrdinalIgnoreCase) || previous.Port != next.Port || previous.UseTls != next.UseTls
        || previous.Username != next.Username || previous.ClientId != next.ClientId || previous.Password != next.Password
        || previous.AccessToken != next.AccessToken || previous.PlatformGatewayId != next.PlatformGatewayId;
    private sealed record ConfirmationGrant(long Version, string CandidateFingerprint, long OutboxCount,
        DateTimeOffset? OldestOutboxAt, DateTimeOffset CreatedAt);
}
