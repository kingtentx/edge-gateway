using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

public sealed partial class SqliteGatewayStore
{
    public Task<RpcJournalEntry?> GetRpcCommandAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ArgumentException("RPC 审计键无效。", nameof(key));
        return ExecuteAsync<RpcJournalEntry?>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT fingerprint,response_topic,response_payload,created_ms FROM rpc_commands WHERE command_key=$key";
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new RpcJournalEntry(key, reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))) : null;
        }, cancellationToken);
    }

    public Task<RpcJournalClaim> ClaimRpcCommandAsync(string key, string fingerprint, int maxEntries, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > 128)
            throw new ArgumentException("RPC 审计键或指纹无效。");
        if (maxEntries is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        return ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT fingerprint,response_topic,response_payload,created_ms FROM rpc_commands WHERE command_key=$key";
            command.Parameters.AddWithValue("$key", key);
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                    return new RpcJournalClaim(false, new RpcJournalEntry(key, reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
            }
            command.CommandText = "SELECT COUNT(*) FROM rpc_commands";
            if (Convert.ToInt64(command.ExecuteScalar()) >= maxEntries)
                throw new InvalidOperationException($"远程写入审计已达到 {maxEntries} 条上限；新写入被拒绝，已记录请求仍可查询重放。请先导出并维护命令审计数据库。");
            var now = DateTimeOffset.UtcNow;
            command.CommandText = "INSERT INTO rpc_commands(command_key,fingerprint,created_ms) VALUES($key,$fingerprint,$created)";
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
            transaction.Commit();
            return new RpcJournalClaim(true, new RpcJournalEntry(key, fingerprint, null, null, now));
        }, cancellationToken);
    }

    public Task CompleteRpcCommandAsync(string key, string responseTopic, string responsePayload, CancellationToken cancellationToken = default)
    {
        if (responseTopic.Length > 4000 || responsePayload.Length > 131072) throw new ArgumentException("RPC 审计响应超过允许长度。");
        return ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE rpc_commands SET response_topic=$topic,response_payload=$payload WHERE command_key=$key AND response_payload IS NULL";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$topic", responseTopic);
            command.Parameters.AddWithValue("$payload", responsePayload);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("RPC 命令不存在或已完成，禁止覆盖原始审计结果。");
            return true;
        }, cancellationToken);
    }
}
