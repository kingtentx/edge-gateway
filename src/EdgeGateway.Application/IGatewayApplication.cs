using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Application;

/// <summary>Driver metadata and validation for editors; never exposes a protocol factory or connection.</summary>
public interface IDriverCatalog
{
    IReadOnlyList<DriverDescriptor> Drivers { get; }
    DriverDescriptor GetDescriptor(string driverId);
    IReadOnlyList<string> Validate(DeviceConfiguration configuration);
    IReadOnlyList<string> ValidateConfiguration(GatewayConfiguration configuration);
}

public sealed record ConfigurationApplyOptions
{
    public Guid? MqttDestinationConfirmation { get; init; }
}

public sealed record ConfigurationChangePreview(
    bool RequiresMqttDestinationConfirmation, Guid? ConfirmationToken, long PendingOutboxMessages,
    MqttConfiguration PreviousMqtt, MqttConfiguration ProposedMqtt);

public sealed record DriverListItem(string Id, string Name, string Version, string AssemblyName,
    string Sha256, string Source, string Status, bool IsAvailable);
public sealed record DriverDeviceReference(Guid Id, string Name, bool IsRunning);
public sealed record DriverDetails(DriverListItem Driver, DriverDescriptor? Descriptor, IReadOnlyList<DriverDeviceReference> Devices,
    IReadOnlyList<string> RelatedDriverIds);

/// <summary>
/// The desktop application boundary. Configuration commands validate and persist before
/// publishing a new snapshot; callers must await success before closing an editor.
/// </summary>
public interface IGatewayApplication : IAsyncDisposable
{
    IDriverCatalog Drivers { get; }
    IGatewayLog Log { get; }
    IReadOnlyList<DriverPluginInfo> GetInstalledDriverPlugins() => [];
    Task<IReadOnlyList<DriverListItem>> GetDriverListAsync(CancellationToken cancellationToken = default);
    Task<DriverDetails> GetDriverDetailsAsync(string driverId, CancellationToken cancellationToken = default);
    Task<string> InstallDriverPluginAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task<string> UpdateDriverPluginAsync(string driverId, string sourcePath, CancellationToken cancellationToken = default);
    Task<string> UninstallDriverPluginAsync(string driverId, CancellationToken cancellationToken = default);
    bool IsRunning { get; }
    bool IsMqttConnected { get; }
    bool IsDeviceRunning(Guid deviceId);
    GatewayConfiguration GetConfiguration();
    IReadOnlyList<string> ValidateConfiguration(GatewayConfiguration candidate);
    Task<ConfigurationChangePreview> PreviewConfigurationAsync(GatewayConfiguration candidate, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> ApplyConfigurationAsync(GatewayConfiguration candidate, ConfigurationApplyOptions? options = null, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> ReplaceConfigurationAsync(GatewayConfiguration candidate, ConfigurationApplyOptions? options = null, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> AddDeviceAsync(DeviceConfiguration device, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> UpdateDeviceAsync(DeviceConfiguration device, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> AppendDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> AddTagAsync(Guid deviceId, TagConfiguration tag, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> UpdateTagAsync(Guid deviceId, TagConfiguration tag, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> DeleteTagAsync(Guid deviceId, Guid tagId, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> ReplaceDeviceTagsAsync(Guid deviceId, IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> SaveDeviceGroupAsync(string? originalName, string name, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> DeleteDeviceGroupAsync(string name, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> UpsertChannelAsync(ChannelConfiguration channel, bool isNew, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> DeleteChannelAsync(Guid channelId, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> ApplyGlobalSettingsAsync(GatewayConfiguration draft, ConfigurationApplyOptions? options = null, CancellationToken cancellationToken = default);
    Task<GatewayConfiguration> ReadSavedConfigurationAsync(CancellationToken cancellationToken = default);
    Task<GatewayConfiguration> ReadConfigurationFileAsync(string path, CancellationToken cancellationToken = default);
    Task ExportConfigurationAsync(string path, CancellationToken cancellationToken = default);
    Task<DeviceConfigurationWorkbook> ReadDeviceWorkbookAsync(string path, CancellationToken cancellationToken = default);
    DeviceWorkbookPreview PreviewDeviceWorkbook(DeviceConfigurationWorkbook workbook, bool updateExisting);
    Task<ConfigurationApplyResult> ImportDeviceWorkbookAsync(DeviceConfigurationWorkbook workbook, bool updateExisting, CancellationToken cancellationToken = default);
    Task ExportDeviceWorkbookAsync(string path, Guid? deviceId = null, bool template = false, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> ImportTagFileAsync(Guid deviceId, byte[] content, TagCsvImportMode mode, CancellationToken cancellationToken = default);
    Task<byte[]> ReadTagWorkbookAsync(string path, CancellationToken cancellationToken = default);
    Task ExportTagWorkbookAsync(string path, Guid? deviceId = null, bool template = false, CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StartDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task StopDeviceAsync(Guid deviceId);
    Task StopAsync();
    IReadOnlyList<TelemetrySample> GetLatest();
    TelemetrySample? GetLatestSample(Guid tagId);
    IReadOnlyList<DeviceRuntimeStatus> GetDeviceStatuses();
    GatewayOverviewSummary GetOverviewSummary();
    Task<ServerResourceSnapshot> GetServerResourcesAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<DeviceCommunicationStatistics> GetCommunicationStatistics();
    TelemetryPipelineStatus GetTelemetryStatus();
    RpcRuntimeStatus GetRpcStatus();
    Task<DeviceProbeResult> ProbeDeviceAsync(Guid deviceId, Guid? tagId = null, CancellationToken cancellationToken = default);
    Task<DeviceProbeResult> ProbeDeviceAsync(Guid deviceId, Guid? tagId, DeviceProbeOptions options, CancellationToken cancellationToken = default);
    Task<WriteCommandResult> WriteVerifiedAsync(Guid deviceId, Guid tagId, string engineeringValue,
        WriteVerificationOptions? options = null, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> ApplyLoggingSettingsAsync(GatewayLoggingConfiguration settings, CancellationToken cancellationToken = default);
    Task<ConfigurationApplyResult> ApplyDatabaseSettingsAsync(IReadOnlyList<DatabaseConnectionConfiguration> connections,
        IReadOnlyList<DatabaseWriteGroupConfiguration> groups, CancellationToken cancellationToken = default);
    Task<DatabaseConnectionTestResult> TestDatabaseConnectionAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseTableInfo>> GetDatabaseTablesAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseColumnInfo>> GetDatabaseColumnsAsync(DatabaseConnectionConfiguration connection, DatabaseTableInfo table, CancellationToken cancellationToken = default);
    Task CreateDatabasePointTableAsync(DatabaseConnectionConfiguration connection, DatabaseTableInfo table, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TelemetrySample>> QueryExternalHistoryAsync(Guid groupId, HistoryQuery query, CancellationToken cancellationToken = default);
    IReadOnlyList<ExternalDatabaseStatus> GetDatabaseStatuses();
    GatewayLoggingStatus GetLoggingStatus();
    Task<DatabaseWritePreview> PreviewDatabaseWriteAsync(DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group, CancellationToken cancellationToken = default);
    Task<GatewayLogQueryResult> QueryLogFilesAsync(GatewayLogQuery query, CancellationToken cancellationToken = default);
    Task ExportLogQueryAsync(GatewayLogQueryResult result, string path, CancellationToken cancellationToken = default);
    Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
    Task<AcquisitionSpoolStatistics> GetAcquisitionSpoolStatisticsAsync(CancellationToken cancellationToken = default);
}
