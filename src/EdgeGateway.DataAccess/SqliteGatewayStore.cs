using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.DataAccess;

/// <summary>Serialized SQLite access, WAL, UTC timestamps, durable at-least-once outbox. Database work runs off the UI thread.</summary>
public sealed class SqliteGatewayStore(string path) : IGatewayStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is not null) return;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
                try
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA user_version";
                    var schema = Convert.ToInt32(command.ExecuteScalar());
                    if (schema > 1) throw new InvalidDataException($"数据库版本 {schema} 高于程序支持的版本 1。");
                    command.CommandText = """
                        PRAGMA journal_mode=WAL;
                        PRAGMA synchronous=FULL;
                        PRAGMA busy_timeout=5000;
                        CREATE TABLE IF NOT EXISTS samples (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            device_id TEXT NOT NULL,
                            tag_id TEXT NOT NULL,
                            timestamp_ms INTEGER NOT NULL,
                            sample_json TEXT NOT NULL);
                        CREATE INDEX IF NOT EXISTS ix_samples_time ON samples(timestamp_ms);
                        CREATE INDEX IF NOT EXISTS ix_samples_tag_time ON samples(tag_id, timestamp_ms);
                        CREATE TABLE IF NOT EXISTS outbox (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            topic TEXT NOT NULL,
                            payload TEXT NOT NULL,
                            created_ms INTEGER NOT NULL);
                        PRAGMA user_version=1;
                        """;
                    command.ExecuteNonQuery();
                    _connection = connection;
                }
                catch { connection.Dispose(); throw; }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task AppendSamplesAsync(IReadOnlyList<TelemetrySample> samples, CancellationToken cancellationToken = default)
    {
        var rows = samples.Select(s => (Sample: s, Json: JsonSerializer.Serialize(s))).ToArray();
        return ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO samples(device_id,tag_id,timestamp_ms,sample_json) VALUES($device,$tag,$time,$json)";
            var device = command.Parameters.Add("$device", SqliteType.Text);
            var tag = command.Parameters.Add("$tag", SqliteType.Text);
            var time = command.Parameters.Add("$time", SqliteType.Integer);
            var json = command.Parameters.Add("$json", SqliteType.Text);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                device.Value = row.Sample.DeviceId.ToString("D");
                tag.Value = row.Sample.TagId.ToString("D");
                time.Value = row.Sample.Timestamp.ToUnixTimeMilliseconds();
                json.Value = row.Json;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TelemetrySample>> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        if (query.From > query.To) throw new ArgumentException("查询开始时间不能晚于结束时间。", nameof(query));
        return ExecuteAsync<IReadOnlyList<TelemetrySample>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sample_json FROM samples WHERE timestamp_ms >= $from AND timestamp_ms <= $to
                  AND ($device IS NULL OR device_id=$device) AND ($tag IS NULL OR tag_id=$tag)
                ORDER BY timestamp_ms DESC, id DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$from", query.From.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", query.To.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$device", (object?)query.DeviceId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$tag", (object?)query.TagId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 10000));
            using var reader = command.ExecuteReader();
            var samples = new List<TelemetrySample>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = JsonSerializer.Deserialize<TelemetrySample>(reader.GetString(0))!;
                samples.Add(sample with { RawValue = Primitive(sample.RawValue), Value = Primitive(sample.Value) });
            }
            return samples;
        }, cancellationToken);
    }

    public Task<bool> EnqueueAsync(string topic, string payload, int maxMessages, CancellationToken cancellationToken = default)
    {
        if (maxMessages < 1) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        return ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM outbox";
            if (Convert.ToInt64(command.ExecuteScalar()) >= maxMessages) return false;
            command.CommandText = "INSERT INTO outbox(topic,payload,created_ms) VALUES($topic,$payload,$time)";
            command.Parameters.AddWithValue("$topic", topic);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(int limit, CancellationToken cancellationToken = default) =>
        ExecuteAsync<IReadOnlyList<OutboxMessage>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,topic,payload,created_ms FROM outbox ORDER BY id LIMIT $limit";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            using var reader = command.ExecuteReader();
            var messages = new List<OutboxMessage>();
            while (reader.Read()) messages.Add(new OutboxMessage(reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
            return messages;
        }, cancellationToken);

    public Task AcknowledgeAsync(long id, CancellationToken cancellationToken = default) => ExecuteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM outbox WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery();
    }, cancellationToken);

    public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) => ExecuteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM samples), (SELECT COUNT(*) FROM outbox)";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new StoreStatistics(reader.GetInt64(0), reader.GetInt64(1));
    }, cancellationToken);

    public Task PruneHistoryAsync(int retentionDays, int maxRows, CancellationToken cancellationToken = default) => ExecuteAsync(connection =>
    {
        if (retentionDays < 1 || maxRows < 1) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM samples WHERE timestamp_ms < $cutoff";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "DELETE FROM samples WHERE id < (SELECT id FROM samples ORDER BY id DESC LIMIT 1 OFFSET $offset)";
        command.Parameters.AddWithValue("$offset", maxRows - 1);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = _connection ?? throw new InvalidOperationException("请先初始化网关数据库。");
            return await Task.Run(() => operation(connection), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static object? Primitive(object? value) => value is not JsonElement json ? value : json.ValueKind switch
    {
        JsonValueKind.Number when json.TryGetInt64(out var n) => n,
        JsonValueKind.Number when json.TryGetUInt64(out var u) => u,
        JsonValueKind.Number when json.TryGetDecimal(out var d) => d,
        JsonValueKind.Number => json.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => json.GetString(),
        JsonValueKind.Null => null,
        _ => json.Clone()
    };

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
