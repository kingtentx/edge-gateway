using Microsoft.Data.Sqlite;

namespace EdgeGateway.Hosting;

/// <summary>Used only by GatewayHost's explicit verification mode; UI never owns database connections.</summary>
internal sealed class ConfigurationWriteFailureScope : IDisposable
{
    private readonly string _connectionString;
    private bool _disposed;

    public ConfigurationWriteFailureScope(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        Execute("""
            CREATE TRIGGER smoke_configuration_write_failure BEFORE INSERT ON configuration_snapshots
            WHEN NEW.slot = 'current'
            BEGIN SELECT RAISE(ABORT, 'smoke_configuration_write_rejected'); END;
            """);
    }

    private void Execute(string statement)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = statement;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Execute("DROP TRIGGER IF EXISTS smoke_configuration_write_failure;");
        _disposed = true;
    }
}
