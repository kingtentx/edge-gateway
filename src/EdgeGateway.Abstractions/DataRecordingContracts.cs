using EdgeGateway.Model;

namespace EdgeGateway.Abstractions;

/// <summary>Provider-specific database I/O. Callers supply validated identifiers and typed, parameterized rows.</summary>
public interface IExternalDatabaseConnector
{
    Task<DatabaseConnectionTestResult> TestConnectionAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(DatabaseConnectionConfiguration connection, DatabaseTableInfo table, CancellationToken cancellationToken = default);
    Task EnsurePointTableAsync(DatabaseConnectionConfiguration connection, DatabaseTableInfo table, CancellationToken cancellationToken = default);
    Task WriteRowsAsync(DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group,
        IReadOnlyList<DatabaseWriteRow> rows, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TelemetrySample>> QueryPointHistoryAsync(DatabaseConnectionConfiguration connection,
        DatabaseWriteGroupConfiguration group, HistoryQuery query, CancellationToken cancellationToken = default);
}

public interface IAcquisitionDataSink : IAsyncDisposable
{
    void UpdateConfiguration(GatewayConfiguration configuration);
    void SetDeviceActive(Guid deviceId, bool active);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task AppendAsync(IReadOnlyList<TelemetrySample> samples, CancellationToken cancellationToken = default);
    Task StopAsync();
    IReadOnlyList<ExternalDatabaseStatus> GetStatuses();
}

public interface IConfigurableGatewayLog : IGatewayLog
{
    void ApplyConfiguration(GatewayLoggingConfiguration configuration);
    void WriteSample(TelemetrySample sample);
}

public interface IGatewayLogStatus
{
    GatewayLoggingStatus GetStatus();
}
