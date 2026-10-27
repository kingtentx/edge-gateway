using System.Text.Json;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class ServerTimeLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edge-server-time-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MidnightRotationUsesServerDateAndKeepsOriginalSampleTime()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Server", TimeSpan.FromHours(8), "Server", "Server");
        var clock = new ServerClock(new(2026, 10, 26, 15, 59, 59, TimeSpan.Zero), zone);
        var logs = Path.Combine(_root, "logs");
        var sampledAt = clock.GetUtcNow().AddMinutes(-30);
        var sample = new TelemetrySample(Guid.NewGuid(), "设备", Guid.NewGuid(), "点", "0", 42, 42,
            SampleQuality.Good, sampledAt, "");
        await using (var log = new GatewayLog(logs, availableDiskBytes: _ => long.MaxValue, timeProvider: clock))
        {
            log.Write(GatewayLogLevel.Information, "午夜前"); log.WriteSample(sample);
            await log.FlushAsync();
            clock.UtcNow = clock.UtcNow.AddSeconds(2);
            log.Write(GatewayLogLevel.Information, "午夜后"); log.WriteSample(sample);
            await log.FlushAsync();
            Assert.True(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).EqualsExact(log.GetEntries()[0].Timestamp));
        }
        foreach (var directory in new[] { logs, Path.Combine(logs, "data") })
            Assert.Equal(new[] { "20261026.log", "20261027.log" }, Directory.GetFiles(directory, "*.log")
                .Select(Path.GetFileName).OrderBy(name => name).ToArray());
        using var afterMidnight = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(logs, "20261027.log")));
        Assert.True(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).EqualsExact(afterMidnight.RootElement.GetProperty("timestamp").GetDateTimeOffset()));
        using var delayedSample = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(logs, "data", "20261027.log")));
        Assert.True(TimeZoneInfo.ConvertTime(sampledAt, zone).EqualsExact(delayedSample.RootElement.GetProperty("timestamp").GetDateTimeOffset()));
    }

    [Theory]
    [InlineData(480)]
    [InlineData(-210)]
    public async Task FileQueryAndCsvExportUseServerOffsetWithoutChangingInstant(int offsetMinutes)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Server", TimeSpan.FromMinutes(offsetMinutes), "Server", "Server");
        var clock = new ServerClock(new(2026, 10, 26, 23, 45, 0, TimeSpan.Zero), zone);
        await using var log = new GatewayLog(Path.Combine(_root, "logs"), availableDiskBytes: _ => long.MaxValue, timeProvider: clock);
        log.Write(GatewayLogLevel.Information, "本地时间"); await log.FlushAsync();
        var result = await log.QueryAsync(new(clock.UtcNow.AddMinutes(-1), clock.UtcNow.AddMinutes(1)) { Category = GatewayLogCategory.Events });
        var expected = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);
        Assert.True(expected.EqualsExact(Assert.Single(result.Entries).Timestamp));
        var export = Path.Combine(_root, "events.csv");
        await log.ExportAsync(result, export);
        var csv = await File.ReadAllTextAsync(export);
        Assert.Contains("时间（采集服务器）", csv);
        Assert.Contains(expected.ToString("O"), csv);
        Assert.DoesNotContain("时间（UTC）", csv);
    }

    private sealed class ServerClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
