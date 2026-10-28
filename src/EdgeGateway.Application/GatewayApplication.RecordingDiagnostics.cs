using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Application;

public sealed partial class GatewayApplication
{
    public async Task<DatabaseWritePreview> PreviewDatabaseWriteAsync(DatabaseConnectionConfiguration connection,
        DatabaseWriteGroupConfiguration group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        var connectionDraft = CaptureConnection(connection);
        var groupDraft = GatewayJson.Clone(group);
        var candidate = GetConfiguration();
        candidate.Databases = [connectionDraft]; candidate.DatabaseWriteGroups = [groupDraft];
        var errors = DataRecordingConfigurationValidator.Validate(candidate);
        if (errors.Count > 0) throw InvalidConfiguration(errors);
        var columns = await ExternalDatabases().GetColumnsAsync(connectionDraft, CheckTable(new(groupDraft.Schema, groupDraft.Table)), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Capture after remote I/O; do not label a snapshot taken before a long schema request as fresh.
        candidate = GetConfiguration();
        var active = candidate.Devices.Where(device => device.Enabled && IsDeviceRunning(device.Id)).Select(device => device.Id).ToHashSet();
        var enabled = candidate.Devices.Where(device => device.Enabled).SelectMany(device => device.Tags.Where(tag => tag.Enabled)).Select(tag => tag.Id).ToHashSet();
        return DatabaseWriteInspector.Inspect(connectionDraft, groupDraft, columns, GetLatest().ToDictionary(sample => sample.TagId), enabled, active, DateTimeOffset.UtcNow);
    }

    public Task<GatewayLogQueryResult> QueryLogFilesAsync(GatewayLogQuery query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return (Log as IGatewayLogArchive ?? throw new InvalidOperationException("当前日志实现不支持文件查询。")).QueryAsync(query, cancellationToken);
    }

    public Task ExportLogQueryAsync(GatewayLogQueryResult result, string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return (Log as IGatewayLogArchive ?? throw new InvalidOperationException("当前日志实现不支持文件导出。")).ExportAsync(result, path, cancellationToken);
    }

    public async Task<SampleLogHistoryResult> QuerySampleLogHistoryAsync(HistoryQuery query, int scanLimitMb = 32,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var archive = await QueryLogFilesAsync(new(query.From, query.To, Limit: query.Limit, ScanLimitMb: scanLimitMb)
        {
            Category = GatewayLogCategory.Samples, DeviceId = query.DeviceId, TagId = query.TagId
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return SampleLogHistory.FromArchive(archive);
    }
}
