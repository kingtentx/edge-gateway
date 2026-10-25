using System.Text.Json;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class GatewayLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edge-log-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SeverityFilteringUsesAnIndependentConfigurationSnapshot()
    {
        await using var log = new GatewayLog(_directory);
        var config = new GatewayLoggingConfiguration { MinimumLevel = GatewayLogLevel.Warning };
        log.ApplyConfiguration(config);
        config.MinimumLevel = GatewayLogLevel.Error;
        log.Write(GatewayLogLevel.Information, "不应写入");
        log.Write(GatewayLogLevel.Warning, "警告\n单行记录");
        await log.FlushAsync();
        var line = Assert.Single(await ReadOpenLogLinesAsync(Assert.Single(Directory.GetFiles(_directory, "*.log"))));
        using var json = JsonDocument.Parse(line);
        Assert.Equal("Warning", json.RootElement.GetProperty("level").GetString());
        Assert.Equal("警告 单行记录", json.RootElement.GetProperty("message").GetString());
        Assert.Single(log.GetEntries());
    }

    [Fact]
    public async Task SamplesAreStructuredAndShutdownDrainsAcceptedEntries()
    {
        var log = new GatewayLog(_directory);
        var sample = new TelemetrySample(Guid.NewGuid(), "设备", Guid.NewGuid(), "计数", "0", ulong.MaxValue,
            ulong.MaxValue, SampleQuality.Good, DateTimeOffset.UtcNow, "件");
        for (var i = 0; i < 100; i++) log.WriteSample(sample);
        await log.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "data"), "*.log")));
        Assert.Equal(100, lines.Length);
        using var document = JsonDocument.Parse(lines[0]);
        var root = document.RootElement;
        Assert.Equal(ulong.MaxValue, root.GetProperty("value").GetUInt64());
        Assert.Equal(ulong.MaxValue, root.GetProperty("rawValue").GetUInt64());
        Assert.Equal("Good", root.GetProperty("quality").GetString());
        Assert.Equal(sample.Timestamp, root.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Equal(sample.DeviceId, root.GetProperty("deviceId").GetGuid());
        Assert.Equal(sample.TagId, root.GetProperty("tagId").GetGuid());
        Assert.Equal("件", root.GetProperty("unit").GetString());
        foreach (var field in new[] { "level", "kind", "sample", "numericValue", "error" })
            Assert.False(root.TryGetProperty(field, out _));
        Assert.Equal(0, log.PendingEntries);
        Assert.Equal(0, log.DroppedEntries);
    }

    [Fact]
    public async Task CompactSamplesOmitEmptyMetadataButPreserveFailedReadQualityAndError()
    {
        var log = new GatewayLog(_directory, availableDiskBytes: _ => long.MaxValue);
        var sample = new TelemetrySample(Guid.NewGuid(), "设备", Guid.NewGuid(), "点位", "0", 42, 42,
            SampleQuality.Good, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)), "");
        log.WriteSample(sample);
        log.WriteSample(sample with { RawValue = null, Value = null, Quality = SampleQuality.Bad, Error = "读取超时" });
        await log.DisposeAsync();
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "data"), "*.log"));
        Assert.Matches(@"\A[0-9]{8}\.log\z", Path.GetFileName(path));
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        using var good = JsonDocument.Parse(lines[0]);
        Assert.False(good.RootElement.TryGetProperty("unit", out _));
        Assert.False(good.RootElement.TryGetProperty("error", out _));
        Assert.Equal(TimeSpan.Zero, good.RootElement.GetProperty("timestamp").GetDateTimeOffset().Offset);
        using var bad = JsonDocument.Parse(lines[1]);
        Assert.Equal("Bad", bad.RootElement.GetProperty("quality").GetString());
        Assert.Equal("读取超时", bad.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, bad.RootElement.GetProperty("rawValue").ValueKind);
        Assert.Equal(JsonValueKind.Null, bad.RootElement.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task EventsAndSamplesUseSeparateDirectoriesAndRetentionBudgets()
    {
        await using var log = new GatewayLog(_directory, availableDiskBytes: _ => long.MaxValue);
        log.ApplyConfiguration(new() { MaxFileSizeMb = 1, RetainedFileCount = 2 });
        var sampleDirectory = Path.Combine(_directory, "data");
        var value = new string('x', 40_000);
        for (var i = 0; i < 40; i++)
        {
            log.Write(GatewayLogLevel.Information, value);
            log.WriteSample(new(Guid.NewGuid(), "设备", Guid.NewGuid(), "点位", "0", value, value,
                SampleQuality.Good, DateTimeOffset.UtcNow, ""));
        }
        await log.FlushAsync();
        Assert.Equal(_directory, log.GetStatus().DirectoryPath);
        Assert.Equal(sampleDirectory, log.GetStatus().SampleDirectoryPath);
        var events = Directory.GetFiles(_directory, "*.log");
        var samples = Directory.GetFiles(sampleDirectory, "*.log");
        Assert.Equal(2, events.Length);
        Assert.Equal(2, samples.Length);
        foreach (var file in events.Concat(samples))
        {
            Assert.InRange(new FileInfo(file).Length, 1L, 1024L * 1024);
            foreach (var line in await ReadOpenLogLinesAsync(file))
            {
                using var json = JsonDocument.Parse(line);
                if (Path.GetDirectoryName(file) == sampleDirectory)
                {
                    Assert.True(json.RootElement.TryGetProperty("rawValue", out _));
                    Assert.False(json.RootElement.TryGetProperty("kind", out _));
                }
                else Assert.Equal("event", json.RootElement.GetProperty("kind").GetString());
            }
        }
        Assert.Equal(0, log.DroppedEntries);
    }

    [Fact]
    public async Task SampleDirectoryFailureDoesNotStopProgramLogging()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "data"), "directory is blocked");
        await using var log = new GatewayLog(_directory, availableDiskBytes: _ => long.MaxValue);
        log.WriteSample(new(Guid.NewGuid(), "设备", Guid.NewGuid(), "点位", "0", 1, 1, SampleQuality.Good, DateTimeOffset.UtcNow, ""));
        log.Write(GatewayLogLevel.Error, "程序日志仍写入");
        await log.FlushAsync();
        Assert.Equal(1, log.FailedEntries);
        var line = Assert.Single(await ReadOpenLogLinesAsync(Assert.Single(Directory.GetFiles(_directory, "*.log"))));
        Assert.Contains("程序日志仍写入", line);
    }

    [Fact]
    public async Task FileRotationHonorsByteLimitAndTotalRetainedFileCount()
    {
        await using var log = new GatewayLog(_directory);
        log.ApplyConfiguration(new GatewayLoggingConfiguration { MaxFileSizeMb = 1, RetainedFileCount = 2 });
        var message = new string('x', 100_000);
        for (var i = 0; i < 40; i++) log.Write(GatewayLogLevel.Information, message);
        await log.FlushAsync();
        var files = Directory.GetFiles(_directory, "*.log");
        Assert.Equal(2, files.Length);
        Assert.All(files, path => Assert.Matches(@"\A[0-9]{8}(?:\.[0-9]{3,})?\.log\z", Path.GetFileName(path)));
        Assert.Single(files, path => Path.GetFileNameWithoutExtension(path).Length == 8);
        Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1L, 1024L * 1024));
        Assert.Equal(0, log.DroppedEntries);
    }

    [Theory]
    [InlineData("gateway-20000101.log")]
    [InlineData("20000101.log")]
    [InlineData("20000101.001.log")]
    public async Task RetentionRemovesExpiredFilesAndSampleLoggingCanBeDisabled(string fileName)
    {
        Directory.CreateDirectory(_directory);
        var expired = Path.Combine(_directory, fileName);
        await File.WriteAllTextAsync(expired, "old");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-20));
        await using var log = new GatewayLog(_directory);
        log.ApplyConfiguration(new GatewayLoggingConfiguration { RetainedDays = 7, LogAcquisitionSamples = false });
        log.WriteSample(new TelemetrySample(Guid.NewGuid(), "设备", Guid.NewGuid(), "点", "0", 1, 1, SampleQuality.Good, DateTimeOffset.UtcNow, ""));
        log.Write(GatewayLogLevel.Information, "正常记录");
        await log.FlushAsync();
        Assert.False(File.Exists(expired));
        var lines = await ReadOpenLogLinesAsync(Assert.Single(Directory.GetFiles(_directory, "*.log")));
        Assert.Single(lines);
        Assert.DoesNotContain("\"kind\":\"sample\"", lines[0]);
    }

    [Fact]
    public async Task QueueSaturationIsBoundedAndDroppedEntriesAreVisible()
    {
        var log = new GatewayLog(_directory, fileQueueCapacity: 1);
        var message = new string('x', 100_000);
        for (var i = 0; i < 2000; i++) log.Write(GatewayLogLevel.Information, message);
        await log.DisposeAsync();
        Assert.True(log.DroppedEntries > 0);
        Assert.Equal(0, log.PendingEntries);
    }

    [Fact]
    public async Task DiskFailureIsReportedWithoutThrowingFromTheAcquisitionCaller()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "occupied");
        await using var log = new GatewayLog(blocked);
        log.Write(GatewayLogLevel.Warning, "尝试写入");
        await log.FlushAsync();
        Assert.Equal(1, log.FailedEntries);
        Assert.Equal(1, log.DroppedEntries);
        Assert.NotNull(log.FileError);
        Assert.Contains(log.GetEntries(), entry => entry.Message.Contains("日志文件写入失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizedSampleIsRejectedBeforeQueueAdmissionAndDoesNotRetainItsValue()
    {
        await using var log = new GatewayLog(_directory);
        var huge = new string('x', 2 * 1024 * 1024);
        log.WriteSample(new TelemetrySample(Guid.NewGuid(), "设备", Guid.NewGuid(), "大文本", "0", huge, huge,
            SampleQuality.Good, DateTimeOffset.UtcNow, ""));
        Assert.Equal(1, log.DroppedEntries);
        Assert.Equal(0, log.PendingEntries);
        Assert.Equal(0, log.PendingBytes);
        Assert.Contains("1 M", log.FileError);
        Assert.DoesNotContain(log.GetEntries(), entry => entry.Message.Contains(huge, StringComparison.Ordinal));
        log.Write(GatewayLogLevel.Information, "后续正常记录");
        await log.FlushAsync();
        var line = Assert.Single(await ReadOpenLogLinesAsync(Assert.Single(Directory.GetFiles(_directory, "*.log"))));
        Assert.Contains("后续正常记录", line);
        Assert.Equal(1, log.DroppedEntries);
    }

    [Fact]
    public async Task ExcessivelyComplexJsonIsRejectedWithoutFullPayloadStringAllocation()
    {
        await using var log = new GatewayLog(_directory);
        using var structured = JsonDocument.Parse("[" + string.Join(',', Enumerable.Repeat("1", 5000)) + "]");
        var sample = new TelemetrySample(Guid.NewGuid(), "设备", Guid.NewGuid(), "数组", "0", structured.RootElement,
            structured.RootElement, SampleQuality.Good, DateTimeOffset.UtcNow, "");
        log.WriteSample(sample);
        Assert.Equal(1, log.DroppedEntries);
        Assert.Equal(0, log.PendingBytes);
        Assert.Contains("结构复杂度", log.FileError);
        await log.FlushAsync();
        Assert.False(Directory.Exists(_directory));
    }

    private static async Task<string[]> ReadOpenLogLinesAsync(string path)
    {
        // Windows requires readers to share write access with the logger's existing write handle.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line) lines.Add(line);
        return lines.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
