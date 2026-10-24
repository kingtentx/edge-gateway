using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.DataAccess;

/// <summary>
/// SQLite contains configuration only. Current/previous configuration are replaced in one FULL-synchronous
/// transaction. Only the current configuration schema is accepted.
/// </summary>
public sealed class SqliteConfigurationStore(string path, Func<GatewayConfiguration>? defaultFactory = null) : IConfigurationStore, IAsyncDisposable
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;
    private volatile bool _hasSavedConfiguration;

    /// <summary>Set by LoadAsync/SaveAsync; creating an empty configuration database does not mark defaults saved.</summary>
    public bool HasSavedConfiguration => _hasSavedConfiguration;

    public async Task<GatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = await Task.Run(() => ReadSlot(OpenCore(), "current"), cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                _hasSavedConfiguration = true;
                return Parse(current);
            }
            _hasSavedConfiguration = false;
            return defaultFactory?.Invoke() ?? new GatewayConfiguration();
        }
        finally { _gate.Release(); }
    }

    public async Task<GatewayConfiguration?> LoadPreviousAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var payload = await Task.Run(() => ReadSlot(OpenCore(), "previous"), cancellationToken).ConfigureAwait(false);
            return payload is null ? null : Parse(payload);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(GatewayConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion != 3) throw new InvalidDataException("仅支持配置版本 3。");
        // Serialization and validation failures happen before opening or changing the database.
        var payload = JsonSerializer.Serialize(configuration, GatewayJson.Options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() => SaveCore(OpenCore(), payload, cancellationToken), cancellationToken).ConfigureAwait(false);
            _hasSavedConfiguration = true;
        }
        finally { _gate.Release(); }
    }

    private SqliteConnection OpenCore()
    {
        if (_connection is not null) return _connection;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version is not (0 or 3))
                throw new InvalidDataException($"配置数据库版本 {version} 不受支持；请使用当前版本的配置数据库。");
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS configuration_versions (
                    slot TEXT PRIMARY KEY NOT NULL CHECK(slot IN ('current','previous')),
                    schema_version INTEGER NOT NULL,
                    payload TEXT NOT NULL,
                    saved_ms INTEGER NOT NULL);
                PRAGMA user_version=3;
                """;
            command.ExecuteNonQuery();
            return _connection = connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static string? ReadSlot(SqliteConnection connection, string slot)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM configuration_versions WHERE slot=$slot";
        command.Parameters.AddWithValue("$slot", slot);
        return command.ExecuteScalar() as string;
    }

    private static void SaveCore(SqliteConnection connection, string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM configuration_versions WHERE slot='previous';
            INSERT INTO configuration_versions(slot,schema_version,payload,saved_ms)
                SELECT 'previous',schema_version,payload,saved_ms FROM configuration_versions WHERE slot='current';
            INSERT INTO configuration_versions(slot,schema_version,payload,saved_ms) VALUES('current',3,$payload,$time)
                ON CONFLICT(slot) DO UPDATE SET schema_version=3,payload=excluded.payload,saved_ms=excluded.saved_ms;
            """;
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit(); // After commit, cancellation cannot report an uncommitted configuration.
    }

    private static GatewayConfiguration Parse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return ConfigurationJson.Deserialize(document.RootElement);
        }
        catch (JsonException exception) { throw new InvalidDataException("配置数据库中的 JSON 已损坏；当前及上一版本未修改。", exception); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
