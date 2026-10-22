using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed class GatewayLog(string? directory = null, int capacity = 2000) : IGatewayLog
{
    private readonly object _sync = new();
    private readonly Queue<GatewayLogEntry> _entries = new();
    private readonly int _capacity = Math.Clamp(capacity, 100, 10000);
    private bool _fileErrorReported;
    private DateOnly? _prunedDate;

    public void Write(GatewayLogLevel level, string message)
    {
        // One logical log line; prevent imported names from forging additional log records.
        var entry = new GatewayLogEntry(DateTimeOffset.UtcNow, level, message.Replace('\r', ' ').Replace('\n', ' '));
        lock (_sync)
        {
            Append(entry);
            if (directory is null) return;
            try
            {
                Directory.CreateDirectory(directory);
                MaintainFiles(directory, entry.Timestamp);
                File.AppendAllText(Path.Combine(directory, $"gateway-{entry.Timestamp:yyyyMMdd}.log"),
                    $"{entry.Timestamp:O} [{level}] {entry.Message}{Environment.NewLine}");
                _fileErrorReported = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_fileErrorReported) Append(new GatewayLogEntry(DateTimeOffset.UtcNow, GatewayLogLevel.Error,
                    "日志文件无法写入，当前日志仍保留在内存，请检查磁盘空间和目录权限。"));
                _fileErrorReported = true;
            }
        }
    }

    public IReadOnlyList<GatewayLogEntry> GetEntries(int limit = 500)
    {
        lock (_sync) return _entries.Reverse().Take(Math.Clamp(limit, 1, _capacity)).ToArray();
    }

    private void Append(GatewayLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity) _entries.Dequeue();
    }

    private void MaintainFiles(string logDirectory, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (_prunedDate != today)
        {
            foreach (var file in Directory.EnumerateFiles(logDirectory, "gateway-*.log"))
            {
                var name = Path.GetFileName(file);
                if (name.Length >= 16 && DateOnly.TryParseExact(name.AsSpan(8, 8), "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var fileDate) && fileDate < today.AddDays(-29))
                    File.Delete(file);
            }
            _prunedDate = today;
        }
        // At most three 10 MiB files per UTC day. Log history beyond these explicit limits is rotated out.
        var current = Path.Combine(logDirectory, $"gateway-{now:yyyyMMdd}.log");
        if (!File.Exists(current) || new FileInfo(current).Length < 10 * 1024 * 1024) return;
        var first = Path.Combine(logDirectory, $"gateway-{now:yyyyMMdd}.1.log");
        var second = Path.Combine(logDirectory, $"gateway-{now:yyyyMMdd}.2.log");
        if (File.Exists(second)) File.Delete(second);
        if (File.Exists(first)) File.Move(first, second);
        File.Move(current, first);
    }
}
