using System.Text.Json;
using EdgeGateway.Model;
using EdgeGateway.Service;
using EdgeGateway.DataAccess;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class SeparatedLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edge-separated-" + Guid.NewGuid().ToString("N"));
    private string Logs => Path.Combine(_root, "logs");
    private static GatewayLogQuery Query(GatewayLogCategory category) => new(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)) { Category = category };
    private static TelemetrySample Sample() => new(Guid.NewGuid(), "设备一", Guid.NewGuid(), "计数", "DB1.0", ulong.MaxValue,
        ulong.MaxValue, SampleQuality.Good, DateTimeOffset.UtcNow, "件");

    [Fact]
    public async Task EventLevelDoesNotSuppressSamplesAndSampleSwitchDoesNotSuppressEvents()
    {
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => long.MaxValue);
        log.ApplyConfiguration(new() { MinimumLevel = GatewayLogLevel.Error });
        log.WriteSample(Sample()); log.Write(GatewayLogLevel.Error, "事件一"); log.Write(GatewayLogLevel.Information, "不记录");
        await log.FlushAsync();
        var samples = await log.QueryAsync(Query(GatewayLogCategory.Samples) with { MinimumLevel = GatewayLogLevel.Error });
        Assert.Equal("18446744073709551615", Assert.Single(samples.Entries).Sample!.RawValue);
        Assert.Equal("事件一", Assert.Single((await log.QueryAsync(Query(GatewayLogCategory.Events))).Entries).Message);
        log.ApplyConfiguration(new() { LogAcquisitionSamples = false });
        log.WriteSample(Sample()); log.Write(GatewayLogLevel.Information, "事件二"); await log.FlushAsync();
        Assert.Single((await log.QueryAsync(Query(GatewayLogCategory.Samples))).Entries);
        Assert.Equal(2, (await log.QueryAsync(Query(GatewayLogCategory.Events))).Entries.Count);
    }

    [Fact]
    public async Task CategoryIsolationDoesNotSpendScanBudgetOnOtherDirectoryAndFiltersBadSamples()
    {
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => long.MaxValue);
        var sample = Sample();
        log.WriteSample(sample);
        log.WriteSample(sample with { Quality = SampleQuality.Bad, RawValue = null, Value = null, Error = "读取超时" });
        log.WriteSample(sample with { DeviceId = Guid.NewGuid(), DeviceName = "另一个设备" });
        await log.FlushAsync();
        await File.WriteAllTextAsync(Path.Combine(Logs, "gateway-broken.log"), new string('x', 2 * 1024 * 1024));
        var result = await log.QueryAsync(Query(GatewayLogCategory.Samples) with { Device = sample.DeviceId.ToString(), Tag = "计数", Quality = SampleQuality.Bad, ScanLimitMb = 1 });
        Assert.False(result.IsPartial); Assert.Equal(0, result.InvalidLines);
        var bad = Assert.Single(result.Entries).Sample!;
        Assert.Equal("读取超时", bad.Error); Assert.Equal("", bad.Value); Assert.Equal(SampleQuality.Bad, bad.Quality);
        Assert.Empty((await log.QueryAsync(Query(GatewayLogCategory.Samples) with { Tag = "不存在" })).Entries);
    }

    [Fact]
    public async Task CsvAndJsonExportUseSnapshotAndProtectFormulaAndLargeInteger()
    {
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => long.MaxValue);
        log.WriteSample(Sample() with { DeviceName = "=SUM(1,2)", TagName = "带\"引号,与换行\n名称" });
        await log.FlushAsync();
        var result = await log.QueryAsync(Query(GatewayLogCategory.Samples));
        log.WriteSample(Sample()); await log.FlushAsync();
        var csv = Path.Combine(_root, "samples.csv"); var json = Path.Combine(_root, "samples.jsonl");
        await log.ExportAsync(result, csv); await log.ExportAsync(result, json);
        var bytes = await File.ReadAllBytesAsync(csv); Assert.Equal(new byte[] { 239, 187, 191 }, bytes[..3]);
        var text = await File.ReadAllTextAsync(csv);
        Assert.Contains("'=SUM(1,2)", text); Assert.Contains("'18446744073709551615", text);
        Assert.Contains("带\"\"引号,与换行\n名称", text);
        var lines = await File.ReadAllLinesAsync(json); Assert.Equal(2, lines.Length);
        Assert.Equal(result.Entries[0].Json, lines[1]);
        await Assert.ThrowsAsync<ArgumentException>(() => log.ExportAsync(result, Path.Combine(Logs, "overwrite.csv")));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await File.WriteAllTextAsync(csv, "original");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.ExportAsync(result, csv, cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(csv));
    }

    [Fact]
    public async Task EventCsvExportsFullMessageBeyondGridPreviewLimit()
    {
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => long.MaxValue);
        var message = new string('x', 2500) + "错误详情末尾";
        log.Write(GatewayLogLevel.Error, message); await log.FlushAsync();
        var result = await log.QueryAsync(Query(GatewayLogCategory.Events));
        Assert.DoesNotContain("错误详情末尾", Assert.Single(result.Entries).Message);
        var path = Path.Combine(_root, "events.csv");
        await log.ExportAsync(result, path);
        Assert.Contains(message, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task IndependentRetentionPoliciesApplyToOwnDirectories()
    {
        Directory.CreateDirectory(Logs); var data = Path.Combine(Logs, "data"); Directory.CreateDirectory(data);
        foreach (var directory in new[] { Logs, data })
            for (var i = 1; i <= 4; i++)
            {
                var path = Path.Combine(directory, $"20200101.{i:000}.log");
                await File.WriteAllTextAsync(path, "old"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-5));
            }
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => long.MaxValue);
        log.ApplyConfiguration(new() { RetainedDays = 2, RetainedFileCount = 2, SampleRetainedDays = 10, SampleRetainedFileCount = 3 });
        log.Write(GatewayLogLevel.Information, "current"); log.WriteSample(Sample()); await log.FlushAsync();
        Assert.Single(Directory.GetFiles(Logs, "*.log")); Assert.Equal(3, Directory.GetFiles(data, "*.log").Length);
        log.ApplyConfiguration(new() { RetainedDays = 30, SampleRetainedDays = 2 }); await log.FlushAsync();
        Assert.Single(Directory.GetFiles(data, "*.log"));
    }

    [Fact]
    public async Task SampleFileSizeIsIndependentOfEventFileSize()
    {
        await using var log = new GatewayLog(Logs, availableDiskBytes: _ => long.MaxValue);
        log.ApplyConfiguration(new() { MaxFileSizeMb = 4, SampleMaxFileSizeMb = 1, SampleRetainedFileCount = 2 });
        var value = new string('a', 40000);
        for (var i = 0; i < 30; i++)
        {
            log.Write(GatewayLogLevel.Information, value); log.WriteSample(Sample() with { RawValue = value, Value = value });
        }
        await log.FlushAsync();
        Assert.Single(Directory.GetFiles(Logs, "*.log"));
        var sampleFiles = Directory.GetFiles(Path.Combine(Logs, "data"), "*.log"); Assert.Equal(2, sampleFiles.Length);
        Assert.All(sampleFiles, file => Assert.InRange(new FileInfo(file).Length, 1, 1024 * 1024));
    }

    [Fact]
    public async Task VersionFourDatabaseIsRejectedWithoutModification()
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, "old.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=4; CREATE TABLE original(value TEXT); INSERT INTO original VALUES('keep')";
            command.ExecuteNonQuery();
        }
        var before = await File.ReadAllBytesAsync(path);
        await using var store = new SqliteConfigurationStore(path);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Contains("需要版本 5", error.Message); Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
