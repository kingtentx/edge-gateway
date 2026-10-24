using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

/// <summary>
/// Durable protocol state, separate from configuration and acquisition data: one immutable file per MQTT
/// message, and one atomically replaced file per RPC claim/result. It never writes acquisition history.
/// </summary>
public sealed class FileGatewayStore(string directory) : IGatewayStore, IRpcCommandJournal
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SortedSet<long> _outboxIds = [];
    private readonly HashSet<string> _rpcFiles = new(StringComparer.Ordinal);
    private FileStream? _directoryLease;
    private bool _initialized;
    private bool _disposed;
    private long _nextId = 1;
    private DateTimeOffset? _oldestOutboxAt;
    private string OutboxDirectory => Path.Combine(_directory, "outbox");
    private string RpcDirectory => Path.Combine(_directory, "rpc");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(_directory);
                _directoryLease = new FileStream(Path.Combine(_directory, "state.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                try
                {
                    Directory.CreateDirectory(OutboxDirectory);
                    Directory.CreateDirectory(RpcDirectory);
                    _outboxIds.Clear();
                    foreach (var file in Directory.EnumerateFiles(OutboxDirectory, "*.json"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!long.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
                            throw new InvalidDataException($"补传文件名无效：{Path.GetFileName(file)}。原文件未删除。");
                        _outboxIds.Add(id);
                    }
                    _rpcFiles.Clear();
                    foreach (var file in Directory.EnumerateFiles(RpcDirectory, "*.json"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (name.Length != 64 || name.Any(c => !Uri.IsHexDigit(c)))
                            throw new InvalidDataException($"RPC 审计文件名无效：{Path.GetFileName(file)}。原文件未删除。");
                        _rpcFiles.Add(name);
                    }
                    _nextId = _outboxIds.Count == 0 ? 1 : checked(_outboxIds.Max + 1);
                    UpdateOldestCore();
                    _initialized = true;
                }
                catch { _directoryLease.Dispose(); _directoryLease = null; throw; }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<bool> EnqueueAsync(string topic, string payload, int maxMessages, CancellationToken cancellationToken = default)
    {
        if (maxMessages < 1) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(payload);
        return ExecuteAsync(() =>
        {
            if (_outboxIds.Count >= maxMessages) return false;
            var message = new OutboxMessage(_nextId, topic, payload, DateTimeOffset.UtcNow);
            WriteRecord(OutboxPath(message.Id), message, replace: false, cancellationToken);
            _outboxIds.Add(message.Id);
            _nextId = checked(message.Id + 1);
            if (_outboxIds.Count == 1) _oldestOutboxAt = message.CreatedAt;
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(int limit, CancellationToken cancellationToken = default) =>
        ExecuteAsync<IReadOnlyList<OutboxMessage>>(() =>
        {
            var messages = new List<OutboxMessage>();
            foreach (var id in _outboxIds.Take(Math.Clamp(limit, 1, 1000)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = ReadRecord<OutboxMessage>(OutboxPath(id));
                if (message.Id != id || message.Topic is null || message.Payload is null)
                    throw new InvalidDataException($"补传文件 {id} 内容无效；原记录保留。");
                messages.Add(message);
            }
            return messages;
        }, cancellationToken);

    public Task AcknowledgeAsync(long id, CancellationToken cancellationToken = default) => ExecuteAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_outboxIds.Contains(id)) return false;
        var wasOldest = id == _outboxIds.Min;
        File.Delete(OutboxPath(id)); // Removal only follows the publisher's acknowledgement contract.
        _outboxIds.Remove(id);
        if (wasOldest) UpdateOldestCore();
        return true;
    }, cancellationToken);

    public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => new StoreStatistics(_outboxIds.Count, _oldestOutboxAt), cancellationToken);

    public Task<RpcJournalEntry?> GetRpcCommandAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        return ExecuteAsync(() => ReadRpcCore(key), cancellationToken);
    }

    public Task<RpcJournalClaim> ClaimRpcCommandAsync(string key, string fingerprint, int maxEntries, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > 128) throw new ArgumentException("RPC 指纹无效。", nameof(fingerprint));
        if (maxEntries is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        return ExecuteAsync(() =>
        {
            if (ReadRpcCore(key) is { } prior) return new RpcJournalClaim(false, prior);
            if (_rpcFiles.Count >= maxEntries)
                throw new InvalidOperationException($"远程写入审计已达到 {maxEntries} 条上限；新写入被拒绝，已记录请求仍可查询重放。请先归档并维护命令审计文件。");
            var entry = new RpcJournalEntry(key, fingerprint, null, null, DateTimeOffset.UtcNow);
            var name = RpcFileName(key);
            WriteRecord(Path.Combine(RpcDirectory, name + ".json"), entry, replace: false, cancellationToken);
            _rpcFiles.Add(name);
            return new RpcJournalClaim(true, entry);
        }, cancellationToken);
    }

    public Task CompleteRpcCommandAsync(string key, string responseTopic, string responsePayload, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(responseTopic);
        ArgumentNullException.ThrowIfNull(responsePayload);
        if (responseTopic.Length > 4000 || responsePayload.Length > 131072) throw new ArgumentException("RPC 审计响应超过允许长度。");
        return ExecuteAsync(() =>
        {
            var entry = ReadRpcCore(key) ?? throw new InvalidOperationException("RPC 命令不存在。");
            if (entry.ResponsePayload is not null) throw new InvalidOperationException("RPC 命令已完成，禁止覆盖原始审计结果。");
            WriteRecord(RpcPath(key), entry with { ResponseTopic = responseTopic, ResponsePayload = responsePayload }, replace: true, cancellationToken);
            return true;
        }, cancellationToken);
    }

    private RpcJournalEntry? ReadRpcCore(string key)
    {
        if (!_rpcFiles.Contains(RpcFileName(key))) return null;
        var entry = ReadRecord<RpcJournalEntry>(RpcPath(key));
        if (entry.Key != key || string.IsNullOrWhiteSpace(entry.Fingerprint)) throw new InvalidDataException("RPC 审计文件身份不匹配或内容损坏；禁止重新执行写入。");
        return entry;
    }

    private void UpdateOldestCore() => _oldestOutboxAt = _outboxIds.Count == 0 ? null : ReadRecord<OutboxMessage>(OutboxPath(_outboxIds.Min)).CreatedAt;
    private string OutboxPath(long id) => Path.Combine(OutboxDirectory, id.ToString("D20", CultureInfo.InvariantCulture) + ".json");
    private string RpcPath(string key) => Path.Combine(RpcDirectory, RpcFileName(key) + ".json");
    private static string RpcFileName(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    private static void ValidateKey(string key)
    { if (string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ArgumentException("RPC 审计键无效。", nameof(key)); }

    private static T ReadRecord<T>(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var record = JsonSerializer.Deserialize<StateRecord<T>>(stream);
            if (record is null || record.Version != 1 || record.Data is null) throw new InvalidDataException($"运行状态文件版本或内容无效：{Path.GetFileName(path)}。");
            return record.Data;
        }
        catch (JsonException exception) { throw new InvalidDataException($"运行状态文件损坏：{Path.GetFileName(path)}。原文件保留。", exception); }
    }

    private static void WriteRecord<T>(string path, T value, bool replace, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StateRecord<T>(1, value));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<T> ExecuteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized) throw new InvalidOperationException("请先初始化网关运行状态目录。");
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_directoryLease is not null) await _directoryLease.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private sealed record StateRecord<T>(int Version, T Data);
}
