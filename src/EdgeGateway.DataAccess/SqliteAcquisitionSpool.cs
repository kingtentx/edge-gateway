using System.Globalization;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.DataAccess;

/// <summary>
/// Write-ahead spool for external-database acquisition rows. It is intentionally separate from
/// gateway.config.db: this database contains telemetry records, not configuration. A batch is
/// committed before external I/O and deleted only after the connector confirms the transaction.
/// </summary>
public sealed class SqliteAcquisitionSpool : IAcquisitionSpool
{
    private const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;
    private const long DefaultMaxRows = 5_000_000;
    private readonly string _path;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _maxBytes;
    private readonly long _maxRows;
    private bool _disposed;
    private long _acknowledged;
    private long _rejected;
    private string? _lastError;

    public SqliteAcquisitionSpool(string path, long maxBytes = DefaultMaxBytes, long maxRows = DefaultMaxRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes < 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxRows < 1) throw new ArgumentOutOfRangeException(nameof(maxRows));
        _path = Path.GetFullPath(path);
        _maxBytes = maxBytes;
        _maxRows = maxRows;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            DefaultTimeout = 10, Cache = SqliteCacheMode.Shared
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=10000;
            CREATE TABLE IF NOT EXISTS acquisition_spool_batches(
                batch_id TEXT PRIMARY KEY NOT NULL,
                group_id TEXT NOT NULL,
                created_ms INTEGER NOT NULL,
                payload TEXT NOT NULL,
                target_fingerprint TEXT NOT NULL DEFAULT '',
                payload_bytes INTEGER NOT NULL CHECK(payload_bytes >= 0),
                row_count INTEGER NOT NULL CHECK(row_count > 0));
            CREATE INDEX IF NOT EXISTS ix_acquisition_spool_group_created
                ON acquisition_spool_batches(group_id, created_ms, batch_id);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(acquisition_spool_batches)";
        using var reader = columns.ExecuteReader();
        var hasFingerprint = false;
        while (reader.Read()) if (string.Equals(reader.GetString(1), "target_fingerprint", StringComparison.OrdinalIgnoreCase)) hasFingerprint = true;
        reader.Close();
        if (!hasFingerprint)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE acquisition_spool_batches ADD COLUMN target_fingerprint TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }
    }

    public async Task<string> AppendAsync(Guid groupId, IReadOnlyList<DatabaseWriteRow> rows, string targetFingerprint = "", CancellationToken cancellationToken = default)
    {
        if (groupId == Guid.Empty) throw new ArgumentException("写入组标识不能为空。", nameof(groupId));
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new ArgumentException("持久缓冲批次不能为空。", nameof(rows));
        var identity = groupId.ToString("N") + "|" + string.Join("|", rows.Select(row => row.EventId));
        var batchId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var created = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(rows.Select(EncodeRow).ToArray());
        var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT 1 FROM acquisition_spool_batches WHERE batch_id=$batch";
                exists.Parameters.AddWithValue("$batch", batchId);
                if (exists.ExecuteScalar() is not null) { transaction.Commit(); return batchId; }
            }
            using (var usage = connection.CreateCommand())
            {
                usage.Transaction = transaction;
                usage.CommandText = "SELECT COALESCE(SUM(payload_bytes),0),COALESCE(SUM(row_count),0) FROM acquisition_spool_batches";
                using var reader = usage.ExecuteReader();
                reader.Read();
                var usedBytes = reader.GetInt64(0);
                var usedRows = reader.GetInt64(1);
                reader.Close();
                if (usedBytes + bytes > _maxBytes || usedRows + rows.Count > _maxRows)
                {
                    _rejected++;
                    _lastError = $"采集持久缓冲已达容量上限（{usedRows}/{_maxRows} 行，{usedBytes}/{_maxBytes} 字节）；未确认本批已持久化。";
                    throw new IOException(_lastError);
                }
            }
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO acquisition_spool_batches(batch_id,group_id,created_ms,payload,target_fingerprint,payload_bytes,row_count) VALUES($batch,$group,$created,$payload,$fingerprint,$bytes,$rows)";
                insert.Parameters.AddWithValue("$batch", batchId);
                insert.Parameters.AddWithValue("$group", groupId.ToString("D"));
                insert.Parameters.AddWithValue("$created", created.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$fingerprint", targetFingerprint ?? "");
                insert.Parameters.AddWithValue("$bytes", bytes);
                insert.Parameters.AddWithValue("$rows", rows.Count);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            _lastError = null;
            return batchId;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _lastError = ex.Message;
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<DurableAcquisitionBatch>> ReadPendingAsync(Guid groupId, int maxBatches = 32, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT batch_id,created_ms,payload,target_fingerprint,payload_bytes FROM acquisition_spool_batches WHERE group_id=$group ORDER BY created_ms,batch_id LIMIT $limit";
            command.Parameters.AddWithValue("$group", groupId.ToString("D"));
            command.Parameters.AddWithValue("$limit", Math.Clamp(maxBatches, 1, 256));
            using var reader = command.ExecuteReader();
            var batches = new List<DurableAcquisitionBatch>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stored = JsonSerializer.Deserialize<StoredRow[]>(reader.GetString(2)) ?? throw new InvalidDataException("持久缓冲批次内容为空。");
                batches.Add(new(groupId, reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    reader.GetString(3), stored.Select(DecodeRow).ToArray(), reader.GetInt64(4)));
            }
            return batches.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    public async Task AcknowledgeAsync(Guid groupId, string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM acquisition_spool_batches WHERE group_id=$group AND batch_id=$batch";
            command.Parameters.AddWithValue("$group", groupId.ToString("D"));
            command.Parameters.AddWithValue("$batch", batchId);
            if (command.ExecuteNonQuery() > 0) _acknowledged++;
        }
        finally { _gate.Release(); }
    }

    public async Task<AcquisitionSpoolStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*),COALESCE(SUM(row_count),0),COALESCE(SUM(payload_bytes),0) FROM acquisition_spool_batches";
            using var reader = command.ExecuteReader(); reader.Read();
            return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), _acknowledged, _rejected, _lastError);
        }
        finally { _gate.Release(); }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static StoredRow EncodeRow(DatabaseWriteRow row) => new(row.EventId, row.RecordedAt,
        row.Values.Select(pair => new StoredValue(pair.Key, TypeName(pair.Value), Format(pair.Value))).ToArray());

    private static DatabaseWriteRow DecodeRow(StoredRow row) =>
        new(row.EventId, row.RecordedAt, row.Values.ToDictionary(value => value.Name, value => Parse(value.Type, value.Value), StringComparer.OrdinalIgnoreCase));

    private static string TypeName(object? value) => value switch
    {
        null => "null", string => "string", bool => "bool", byte => "byte", sbyte => "sbyte",
        short => "short", ushort => "ushort", int => "int", uint => "uint", long => "long", ulong => "ulong",
        decimal => "decimal", double => "double", float => "float", Guid => "guid",
        DateTimeOffset => "datetimeoffset", DateTime => "datetime", byte[] => "bytes", _ => throw new NotSupportedException($"不支持持久缓冲类型：{value.GetType().FullName}")
    };

    private static string? Format(object? value) => value switch
    {
        null => null, bool b => b ? "true" : "false", DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture), byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString()
    };

    private static object? Parse(string type, string? value) => type switch
    {
        "null" => null, "string" => value ?? "", "bool" => bool.Parse(value!),
        "byte" => byte.Parse(value!, CultureInfo.InvariantCulture), "sbyte" => sbyte.Parse(value!, CultureInfo.InvariantCulture),
        "short" => short.Parse(value!, CultureInfo.InvariantCulture), "ushort" => ushort.Parse(value!, CultureInfo.InvariantCulture),
        "int" => int.Parse(value!, CultureInfo.InvariantCulture), "uint" => uint.Parse(value!, CultureInfo.InvariantCulture),
        "long" => long.Parse(value!, CultureInfo.InvariantCulture), "ulong" => ulong.Parse(value!, CultureInfo.InvariantCulture),
        "decimal" => decimal.Parse(value!, CultureInfo.InvariantCulture), "double" => double.Parse(value!, CultureInfo.InvariantCulture),
        "float" => float.Parse(value!, CultureInfo.InvariantCulture), "guid" => Guid.Parse(value!),
        "datetimeoffset" => DateTimeOffset.Parse(value!, CultureInfo.InvariantCulture, DateTimeStyles.None),
        "datetime" => DateTime.Parse(value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "bytes" => Convert.FromBase64String(value!), _ => throw new InvalidDataException("持久缓冲中包含未知的数据类型：" + type)
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private sealed record StoredRow(string EventId, DateTimeOffset RecordedAt, StoredValue[] Values);
    private sealed record StoredValue(string Name, string Type, string? Value);
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
