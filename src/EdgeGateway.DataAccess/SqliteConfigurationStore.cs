using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.DataAccess;

/// <summary>
/// SQLite contains configuration only. Current/previous configuration are replaced in one FULL-synchronous
/// transaction. Only the current configuration schema is accepted.
/// </summary>
public sealed class SqliteConfigurationStore(string path, Func<GatewayConfiguration>? defaultFactory = null) : IConfigurationStore, IDriverPluginStore, IDriverInventoryStore, IAsyncDisposable
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
            var current = await Task.Run(() => ReadConfiguration(OpenCore(), "current"), cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                _hasSavedConfiguration = true;
                return current;
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
            var payload = await Task.Run(() => ReadConfiguration(OpenCore(), "previous"), cancellationToken).ConfigureAwait(false);
            return payload;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(GatewayConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion != 3) throw new InvalidDataException("仅支持配置版本 3。");
        // Capture and scalar validation happen before opening or changing the database.
        var rows = RelationalConfiguration.Capture(configuration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() => RelationalConfiguration.Save(OpenCore(), rows, cancellationToken), cancellationToken).ConfigureAwait(false);
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
            if (version != 0 && version != RelationalConfiguration.StorageVersion)
                throw new InvalidDataException($"配置数据库结构版本 {version} 不受支持；当前需要版本 {RelationalConfiguration.StorageVersion}。旧库不会自动迁移或清空，请使用新的空数据目录。");
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            var tableCount = Convert.ToInt32(command.ExecuteScalar());
            if (version == 0 && tableCount != 0)
                throw new InvalidDataException("配置数据库未声明结构版本且包含已有表；不会覆盖或清空原库。");
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
            if (version == 0)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = RelationalConfiguration.CreateSql + """
                    CREATE TABLE driver_registry (
                        driver_id TEXT PRIMARY KEY NOT NULL COLLATE NOCASE,
                        name TEXT NOT NULL,
                        version TEXT NOT NULL,
                        assembly_name TEXT NOT NULL,
                        sha256 TEXT NOT NULL,
                        plugin_status TEXT,
                        listed INTEGER NOT NULL DEFAULT 0 CHECK(listed IN (0,1)),
                        description TEXT NOT NULL DEFAULT '',
                        supports_write INTEGER NOT NULL DEFAULT 0 CHECK(supports_write IN (0,1)),
                        is_legacy INTEGER NOT NULL DEFAULT 0 CHECK(is_legacy IN (0,1)),
                        source TEXT NOT NULL DEFAULT '',
                        updated_ms INTEGER NOT NULL) STRICT;
                    PRAGMA user_version=5;
                    """;
                command.ExecuteNonQuery();
                transaction.Commit();
                command.Transaction = null;
            }
            return _connection = connection;
        }
        catch { connection.Dispose(); throw; }
    }


    public async Task SaveDriverPluginAsync(DriverPluginInfo plugin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (string.IsNullOrWhiteSpace(plugin.Id) || string.IsNullOrWhiteSpace(plugin.AssemblyName)
            || string.IsNullOrWhiteSpace(plugin.Sha256))
            throw new ArgumentException("驱动插件元数据不完整。", nameof(plugin));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() =>
            {
                using var command = OpenCore().CreateCommand();
                command.CommandText = """
                    INSERT INTO driver_registry(driver_id,name,version,assembly_name,sha256,plugin_status,updated_ms)
                    VALUES($id,$name,$version,$assembly,$sha256,$status,$time)
                    ON CONFLICT(driver_id) DO UPDATE SET
                        name=excluded.name,version=excluded.version,assembly_name=excluded.assembly_name,
                        sha256=excluded.sha256,plugin_status=excluded.plugin_status,updated_ms=excluded.updated_ms;
                    """;
                command.Parameters.AddWithValue("$id", plugin.Id);
                command.Parameters.AddWithValue("$name", plugin.Name);
                command.Parameters.AddWithValue("$version", plugin.Version);
                command.Parameters.AddWithValue("$assembly", plugin.AssemblyName);
                command.Parameters.AddWithValue("$sha256", plugin.Sha256);
                command.Parameters.AddWithValue("$status", plugin.Status);
                command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ReplaceDriverPluginsAsync(IReadOnlyList<string> driverIds, IReadOnlyList<DriverPluginInfo> plugins,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() =>
            {
                var connection = OpenCore();
                using var transaction = connection.BeginTransaction();
                foreach (var id in driverIds)
                {
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM driver_registry WHERE driver_id=$id COLLATE NOCASE;";
                    delete.Parameters.AddWithValue("$id", id);
                    delete.ExecuteNonQuery();
                }
                foreach (var plugin in plugins)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO driver_registry(driver_id,name,version,assembly_name,sha256,plugin_status,updated_ms) VALUES($id,$name,$version,$assembly,$hash,$status,$time);";
                    insert.Parameters.AddWithValue("$id", plugin.Id);
                    insert.Parameters.AddWithValue("$name", plugin.Name);
                    insert.Parameters.AddWithValue("$version", plugin.Version);
                    insert.Parameters.AddWithValue("$assembly", plugin.AssemblyName);
                    insert.Parameters.AddWithValue("$hash", plugin.Sha256);
                    insert.Parameters.AddWithValue("$status", plugin.Status);
                    insert.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<DriverPluginInfo>> GetDriverPluginsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run<IReadOnlyList<DriverPluginInfo>>(() =>
            {
                using var command = OpenCore().CreateCommand();
                command.CommandText = "SELECT driver_id,name,version,assembly_name,sha256,plugin_status FROM driver_registry WHERE plugin_status IS NOT NULL ORDER BY driver_id";
                using var reader = command.ExecuteReader();
                var plugins = new List<DriverPluginInfo>();
                while (reader.Read())
                    plugins.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                return plugins.AsReadOnly();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }


    public async Task ReplaceDriverInventoryAsync(IReadOnlyList<DriverInventoryRecord> drivers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        if (drivers.Any(driver => string.IsNullOrWhiteSpace(driver.Id) || string.IsNullOrWhiteSpace(driver.Name)))
            throw new ArgumentException("驱动清单包含无效标识或名称。", nameof(drivers));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() =>
            {
                // The store owns this shared connection; only the transaction is scoped
                // to the inventory refresh. DisposeAsync releases the connection.
                var connection = OpenCore();
                using var transaction = connection.BeginTransaction();
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "UPDATE driver_registry SET listed=0,description='',supports_write=0,is_legacy=0,source=''; DELETE FROM driver_registry WHERE plugin_status IS NULL";
                    delete.ExecuteNonQuery();
                }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var driver in drivers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seen.Add(driver.Id)) throw new ArgumentException("驱动清单包含重复标识。", nameof(drivers));
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO driver_registry(driver_id,name,description,supports_write,is_legacy,assembly_name,version,sha256,source,updated_ms,listed)
                        VALUES($id,$name,$description,$write,$legacy,$assembly,$version,$sha256,$source,$time,1)
                        ON CONFLICT(driver_id) DO UPDATE SET
                            name=excluded.name,description=excluded.description,supports_write=excluded.supports_write,
                            is_legacy=excluded.is_legacy,assembly_name=excluded.assembly_name,version=excluded.version,
                            sha256=excluded.sha256,source=excluded.source,updated_ms=excluded.updated_ms,listed=1;
                        """;
                    command.Parameters.AddWithValue("$id", driver.Id);
                    command.Parameters.AddWithValue("$name", driver.Name);
                    command.Parameters.AddWithValue("$description", driver.Description ?? "");
                    command.Parameters.AddWithValue("$write", driver.SupportsWrite ? 1 : 0);
                    command.Parameters.AddWithValue("$legacy", driver.IsLegacy ? 1 : 0);
                    command.Parameters.AddWithValue("$assembly", driver.AssemblyName ?? "");
                    command.Parameters.AddWithValue("$version", driver.Version ?? "");
                    command.Parameters.AddWithValue("$sha256", driver.Sha256 ?? "");
                    command.Parameters.AddWithValue("$source", driver.Source ?? "");
                    command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<DriverInventoryRecord>> GetDriverInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run<IReadOnlyList<DriverInventoryRecord>>(() =>
            {
                using var command = OpenCore().CreateCommand();
                command.CommandText = "SELECT driver_id,name,description,supports_write,is_legacy,assembly_name,version,sha256,source FROM driver_registry WHERE listed=1 ORDER BY driver_id";
                using var reader = command.ExecuteReader();
                var records = new List<DriverInventoryRecord>();
                while (reader.Read())
                    records.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetInt32(3) != 0, reader.GetInt32(4) != 0, reader.GetString(5),
                        reader.GetString(6), reader.GetString(7), reader.GetString(8)));
                return records.AsReadOnly();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static GatewayConfiguration? ReadConfiguration(SqliteConnection connection, string slot)
    {
        try { return RelationalConfiguration.Read(connection, slot); }
        catch (Exception exception) when (exception is SqliteException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("配置数据库的表结构或字段已损坏；当前及上一份配置未修改。", exception);
        }
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
