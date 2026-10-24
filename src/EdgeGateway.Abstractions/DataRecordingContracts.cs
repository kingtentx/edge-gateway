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

/// <summary>Read-only bounded log queries and export of the exact displayed result.</summary>
public interface IGatewayLogArchive
{
    Task<GatewayLogQueryResult> QueryAsync(GatewayLogQuery query, CancellationToken cancellationToken = default);
    Task ExportAsync(GatewayLogQueryResult result, string path, CancellationToken cancellationToken = default);
}

/// <summary>Sanitized provider failure. No provider exception or credentials are retained.</summary>
public class DatabaseWriteException(string message, bool isTransient) : InvalidOperationException(message)
{
    public bool IsTransient { get; } = isTransient;
}

public interface IGatewayLogStatus
{
    GatewayLoggingStatus GetStatus();
}
