using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Application;

public sealed partial class GatewayApplication
{
    public Task<ConfigurationApplyResult> ApplyLoggingSettingsAsync(GatewayLoggingConfiguration settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var draft = GatewayJson.Clone(settings);
        return ChangeAsync(candidate => candidate.Logging = draft, cancellationToken);
    }

    public Task<ConfigurationApplyResult> ApplyDatabaseSettingsAsync(IReadOnlyList<DatabaseConnectionConfiguration> connections,
        IReadOnlyList<DatabaseWriteGroupConfiguration> groups, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(groups);
        var connectionDrafts = GatewayJson.Clone(connections).ToList();
        var groupDrafts = GatewayJson.Clone(groups).ToList();
        return ChangeAsync(candidate => { candidate.Databases = connectionDrafts; candidate.DatabaseWriteGroups = groupDrafts; },
            cancellationToken, requireStopped: true);
    }

    public Task<DatabaseConnectionTestResult> TestDatabaseConnectionAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default) =>
        ExternalDatabases().TestConnectionAsync(CaptureConnection(connection), cancellationToken);

    public Task<IReadOnlyList<DatabaseTableInfo>> GetDatabaseTablesAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default) =>
        ExternalDatabases().GetTablesAsync(CaptureConnection(connection), cancellationToken);

    public Task<IReadOnlyList<DatabaseColumnInfo>> GetDatabaseColumnsAsync(DatabaseConnectionConfiguration connection,
        DatabaseTableInfo table, CancellationToken cancellationToken = default) =>
        ExternalDatabases().GetColumnsAsync(CaptureConnection(connection), CheckTable(table), cancellationToken);

    public Task CreateDatabasePointTableAsync(DatabaseConnectionConfiguration connection, DatabaseTableInfo table,
        CancellationToken cancellationToken = default) =>
        ExternalDatabases().EnsurePointTableAsync(CaptureConnection(connection), CheckTable(table), cancellationToken);

    public Task<IReadOnlyList<TelemetrySample>> QueryExternalHistoryAsync(Guid groupId, HistoryQuery query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(query);
        if (query.To < query.From) throw new ArgumentException("结束时间不能早于开始时间。", nameof(query));
        if (query.Limit is < 1 or > 10000) throw new ArgumentException("单次历史查询须为 1–10000 行。", nameof(query));
        var configuration = GetConfiguration();
        var group = configuration.DatabaseWriteGroups.FirstOrDefault(item => item.Id == groupId)
            ?? throw new InvalidOperationException("数据库写入组不存在。");
        if (group.Mode != DatabaseStorageMode.PointRows) throw new InvalidOperationException("内置趋势查询仅适用于标准点表；自定义业务表请使用对应数据库查询工具。");
        var connection = configuration.Databases.FirstOrDefault(item => item.Id == group.ConnectionId)
            ?? throw new InvalidOperationException("数据库连接不存在。");
        return ExternalDatabases().QueryPointHistoryAsync(connection, group, query, cancellationToken);
    }

    public IReadOnlyList<ExternalDatabaseStatus> GetDatabaseStatuses()
    {
        ThrowIfDisposed();
        return _runtime.GetDatabaseStatuses();
    }

    public GatewayLoggingStatus GetLoggingStatus()
    {
        ThrowIfDisposed();
        return Log is IGatewayLogStatus status ? status.GetStatus() : new(0, 0, 0, null);
    }

    private IExternalDatabaseConnector ExternalDatabases()
    {
        ThrowIfDisposed();
        return _externalDatabases ?? throw new InvalidOperationException("当前宿主未配置外部数据库连接器。");
    }

    private static DatabaseConnectionConfiguration CaptureConnection(DatabaseConnectionConfiguration connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var draft = GatewayJson.Clone(connection);
        var errors = DataRecordingConfigurationValidator.ValidateConnection(draft);
        if (errors.Count > 0) throw InvalidConfiguration(errors);
        return draft;
    }

    private static DatabaseTableInfo CheckTable(DatabaseTableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!DataRecordingConfigurationValidator.IsIdentifier(table.Name)
            || (!string.IsNullOrEmpty(table.Schema) && !DataRecordingConfigurationValidator.IsIdentifier(table.Schema)))
            throw new ArgumentException("表名与 schema 应分别填写为字母、数字或下划线组成的标识符。", nameof(table));
        return table;
    }
}
