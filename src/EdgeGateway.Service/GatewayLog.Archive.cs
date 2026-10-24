using System.Text;
using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed partial class GatewayLog
{
    private static readonly UTF8Encoding ArchiveEncoding = new(false, true);
    private const int MaxQueryResultBytes = 4 * 1024 * 1024;
    private const int MaxReadWindowBytes = 64 * 1024 * 1024;

    public Task<GatewayLogQueryResult> QueryAsync(GatewayLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.From > query.To || !Enum.IsDefined(query.MinimumLevel) || query.Text is null || query.Text.Length > 256
            || query.Limit is < 1 or > 2000 || query.ScanLimitMb is < 1 or > 64)
            throw new ArgumentException("日志查询要求开始时间不晚于结束时间、关键词不超过 256 字符、1–2000 行及 1–64 MiB 扫描上限。", nameof(query));
        // Directory enumeration, JSON parsing and sorting must not run on the WinForms thread.
        return Task.Run(() => QueryFilesAsync(query, cancellationToken), cancellationToken);
    }

    private async Task<GatewayLogQueryResult> QueryFilesAsync(GatewayLogQuery query, CancellationToken token)
    {
        var entries = new List<GatewayLogFileEntry>();
        var warnings = new List<string>();
        long scanned = 0; var invalid = 0; var partial = false; var resultBytes = 0;
        if (_directory is null || !Directory.Exists(_directory))
            return new(query, entries, 0, 0, false, ["日志目录尚未建立，或当前宿主只保留内存运行记录。"]);
        var files = Directory.EnumerateFiles(_directory, "gateway-*.log", SearchOption.TopDirectoryOnly)
            .Take(1001).Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal).ToArray();
        if (files.Length > 1000) { partial = true; warnings.Add("文件数超过 1000，查询仅扫描受限文件集合。"); }
        var budget = query.ScanLimitMb * 1024L * 1024;
        foreach (var file in files.Take(1000))
        {
            token.ThrowIfCancellationRequested();
            if (scanned >= budget || entries.Count >= query.Limit || resultBytes >= MaxQueryResultBytes)
            { partial = true; break; }
            try
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                { partial = true; warnings.Add($"已跳过链接文件 {file.Name}。"); continue; }
                await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
                var length = stream.Length; // Fixed read boundary; concurrent appends are not part of this snapshot.
                var count = (int)Math.Min(Math.Min(length, budget - scanned), MaxReadWindowBytes);
                if (count == 0) continue;
                var offset = length - count;
                if (offset > 0) partial = true;
                stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[count]; var read = 0;
                while (read < count)
                {
                    var received = await stream.ReadAsync(buffer.AsMemory(read, count - read), token).ConfigureAwait(false);
                    if (received == 0) { partial = true; break; }
                    read += received;
                }
                scanned += read;
                var first = offset > 0 ? Array.IndexOf(buffer, (byte)'\n', 0, read) + 1 : 0;
                var last = read == 0 ? -1 : Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
                // Never parse a cut first line or an unfinished trailing append as a valid record.
                if (offset > 0 && first == 0 || last < first) { partial = true; continue; }
                if (last != read - 1) partial = true;
                var end = last;
                while (end >= first)
                {
                    token.ThrowIfCancellationRequested();
                    var previous = end > first ? Array.LastIndexOf(buffer, (byte)'\n', end - 1, end - first) : -1;
                    var start = previous < first ? first : previous + 1;
                    var size = end - start;
                    if (size > 0)
                    {
                        if (size > SamplePayloadBudget.MaximumSampleBytes) { invalid++; partial = true; }
                        else
                        {
                            GatewayLogFileEntry? entry;
                            try { entry = ParseArchiveLine(ArchiveEncoding.GetString(buffer, start, size), file.Name, query, ref invalid); }
                            catch (DecoderFallbackException) { invalid++; partial = true; entry = null; }
                            if (entry is not null)
                            {
                                if (entries.Count >= query.Limit || resultBytes + size > MaxQueryResultBytes)
                                { partial = true; break; }
                                entries.Add(entry); resultBytes += size;
                            }
                        }
                    }
                    end = previous;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { partial = true; warnings.Add($"文件 {file.Name} 在查询中被轮转、删除或不可读取；请重新查询。"); }
        }
        if (invalid > 0) partial = true;
        if (invalid > 0) warnings.Add($"忽略了 {invalid} 条损坏或超长记录。");
        if (partial) warnings.Add("这是受限的近期文件尾部查询，不代表全部历史或完整备份；时间/关键词过滤不会扩大扫描覆盖范围，可增加扫描额度。");
        return new(query, entries.OrderByDescending(entry => entry.Timestamp).ToArray(), scanned, invalid, partial, warnings.Distinct().ToArray());
    }

    private static GatewayLogFileEntry? ParseArchiveLine(string line, string fileName, GatewayLogQuery query, ref int invalid)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            var time = root.GetProperty("timestamp").GetDateTimeOffset();
            if (!Enum.TryParse<GatewayLogLevel>(root.GetProperty("level").GetString(), out var level) || !Enum.IsDefined(level))
                throw new JsonException("日志级别无效。");
            var kind = root.GetProperty("kind").GetString();
            if (kind is not ("event" or "sample")) throw new JsonException("日志类别无效。");
            if (time < query.From || time > query.To || level < query.MinimumLevel || !query.IncludeSamples && kind == "sample"
                || query.Text.Length > 0 && !line.Contains(query.Text, StringComparison.OrdinalIgnoreCase)) return null;
            var message = kind == "event" ? root.GetProperty("message").GetString() ?? "" : root.GetProperty("sample").GetRawText();
            if (message.Length > 2000) message = message[..2000] + "…（详情及导出保留完整 JSON）";
            return new(time, level, kind, message, fileName, line);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        { invalid++; return null; }
    }

    public Task ExportAsync(GatewayLogQueryResult result, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var destination = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(destination), ".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("日志导出文件必须使用 .jsonl 扩展名，不能覆盖配置或运行日志。", nameof(path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (_directory is not null && destination.StartsWith(TrailingSeparator(_directory), comparison))
            throw new ArgumentException("请导出到运行日志目录之外，避免影响轮转和查询。", nameof(path));
        if (result.Entries.Count > 2000 || result.Entries.Sum(entry => (long)Encoding.UTF8.GetByteCount(entry.Json)) > MaxQueryResultBytes)
            throw new ArgumentException("导出结果超过受限查询大小。", nameof(result));
        return Task.Run(async () =>
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true))
                    {
                        var header = JsonSerializer.Serialize(new { kind = "export", query = result.Query, count = result.Entries.Count,
                            partial = result.IsPartial, scannedBytes = result.ScannedBytes, warnings = result.Warnings });
                        await writer.WriteLineAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
                        foreach (var entry in result.Entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await writer.WriteLineAsync(entry.Json.AsMemory(), cancellationToken).ConfigureAwait(false);
                        }
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, cancellationToken);
    }

    private static string TrailingSeparator(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
