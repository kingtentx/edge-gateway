using System.Text.Json;
using EdgeGateway.Hosting;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.Application;

public sealed class SampleLogHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edge-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NoDatabaseQueryFiltersStableIdsAndPreservesValuesQualityTimeAndLimits()
    {
        await using var host = await GatewayHost.CreateAsync(Path.Combine(_root, "AppData"),
            pluginDirectory: Path.Combine(_root, "drivers"), runtimeDirectory: _root);
        var app = host.Application;
        Assert.Empty(app.GetConfiguration().Databases);
        var log = Assert.IsType<GatewayLog>(app.Log);
        var device = Guid.NewGuid(); var tag = Guid.NewGuid();
        var time = DateTimeOffset.UtcNow.AddMinutes(-1).ToOffset(TimeSpan.FromHours(-3));
        var sample = new TelemetrySample(device, "设备", tag, "温度", "0", long.MaxValue, long.MaxValue,
            SampleQuality.Good, time, "℃");
        log.WriteSample(sample);
        log.WriteSample(sample with { Value = null, Quality = SampleQuality.Bad, Error = "超时", Timestamp = time.AddSeconds(1) });
        log.WriteSample(sample with { Value = true, Timestamp = time.AddSeconds(2) });
        // A name containing the requested ID must not pass stable-ID filtering.
        log.WriteSample(sample with { DeviceId = Guid.NewGuid(), DeviceName = device.ToString(), Timestamp = time.AddSeconds(3) });
        log.WriteSample(sample with { TagId = Guid.NewGuid(), TagName = tag.ToString(), Timestamp = time.AddSeconds(4) });
        await log.FlushAsync();
        var query = new HistoryQuery(device, tag, time.AddSeconds(-1), time.AddMinutes(1), 10);
        var result = await app.QuerySampleLogHistoryAsync(query);
        Assert.Equal(3, result.Samples.Count); Assert.False(result.IsPartial);
        var number = result.Samples.Single(s => s.Timestamp == time);
        Assert.Equal(long.MaxValue, Assert.IsType<JsonElement>(number.Value).GetInt64());
        Assert.Equal(time.ToLocalTime().Offset, number.Timestamp.Offset);
        var bad = result.Samples.Single(s => s.Quality == SampleQuality.Bad);
        Assert.Null(bad.Value); Assert.Equal("超时", bad.Error);
        var boolean = result.Samples.Single(s => s.Timestamp == time.AddSeconds(2));
        Assert.True(Assert.IsType<bool>(boolean.Value)); Assert.Null(boolean.NumericValue);
        var limited = await app.QuerySampleLogHistoryAsync(query with { Limit = 1 });
        Assert.Single(limited.Samples); Assert.True(limited.IsPartial);
        Assert.Empty((await app.QuerySampleLogHistoryAsync(query with { From = time.AddDays(-2), To = time.AddDays(-1) })).Samples);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.QuerySampleLogHistoryAsync(query, cancellationToken: new(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => app.QuerySampleLogHistoryAsync(query, 65));
    }

    [Fact]
    public async Task LegacySamplesAndDamagedLinesKeepPartialResultEvidence()
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(Path.Combine(logs, "data"));
        var now = DateTimeOffset.Now;
        var sample = new TelemetrySample(Guid.NewGuid(), "旧记录", Guid.NewGuid(), "结构", "0",
            new { code = 1 }, "text", SampleQuality.Uncertain, now, "", "不确定");
        var legacy = JsonSerializer.Serialize(new { timestamp = now, level = "Information", kind = "sample", sample });
        await File.WriteAllTextAsync(Path.Combine(logs, "data", "gateway-legacy.log"), legacy + "\nbroken\n");
        await using var log = new GatewayLog(logs);
        var result = SampleLogHistory.FromArchive(await log.QueryAsync(new(now.AddMinutes(-1), now.AddMinutes(1))
            { Category = GatewayLogCategory.Samples }));
        var read = Assert.Single(result.Samples);
        Assert.Equal("text", read.Value); Assert.Equal(SampleQuality.Uncertain, read.Quality);
        Assert.Equal(1, Assert.IsType<JsonElement>(read.RawValue).GetProperty("code").GetInt32());
        Assert.True(result.IsPartial); Assert.Equal(1, result.InvalidLines); Assert.NotEmpty(result.Warnings);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
