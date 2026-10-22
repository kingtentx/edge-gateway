using EdgeGateway.DataAccess;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class SqliteStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edge-sqlite-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "gateway.db");

    [Fact]
    public async Task HistoryAndUnacknowledgedOutboxSurviveRestart()
    {
        var sample = MakeSample(DateTimeOffset.UtcNow, 42.25);
        await using (var first = new SqliteGatewayStore(DatabasePath))
        {
            await first.InitializeAsync();
            await first.AppendSamplesAsync([sample]);
            Assert.True(await first.EnqueueAsync("test/edge", "{\"messageId\":\"stable-1\"}", 5));
        }
        await using var second = new SqliteGatewayStore(DatabasePath);
        await second.InitializeAsync();
        var rows = await second.QueryHistoryAsync(new HistoryQuery(sample.DeviceId, sample.TagId,
            sample.Timestamp.AddMinutes(-1), sample.Timestamp.AddMinutes(1)));
        Assert.Single(rows);
        Assert.Equal(42.25, rows[0].NumericValue);
        Assert.Equal(sample.Timestamp, rows[0].Timestamp);
        var messages = await second.ReadOutboxAsync(10);
        Assert.Single(messages);
        Assert.Contains("stable-1", messages[0].Payload);
        await second.AcknowledgeAsync(messages[0].Id);
        Assert.Empty(await second.ReadOutboxAsync(10));
    }

    [Fact]
    public async Task FullOutboxRejectsNewRowsWithoutEvictingOldRows()
    {
        await using var store = new SqliteGatewayStore(DatabasePath);
        await store.InitializeAsync();
        Assert.True(await store.EnqueueAsync("topic", "first", 1));
        Assert.False(await store.EnqueueAsync("topic", "second", 1));
        var row = Assert.Single(await store.ReadOutboxAsync(10));
        Assert.Equal("first", row.Payload);
        await store.AcknowledgeAsync(row.Id);
        Assert.True(await store.EnqueueAsync("topic", "third", 1));
        Assert.Equal("third", Assert.Single(await store.ReadOutboxAsync(10)).Payload);
    }

    [Fact]
    public async Task RetentionRemovesOldAndExcessHistoryButNeverOutbox()
    {
        await using var store = new SqliteGatewayStore(DatabasePath);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await store.AppendSamplesAsync([MakeSample(now.AddDays(-60), 1), MakeSample(now.AddMinutes(-2), 2),
            MakeSample(now.AddMinutes(-1), 3), MakeSample(now, 4)]);
        await store.EnqueueAsync("topic", "unsent", 10);
        await store.PruneHistoryAsync(30, 2);
        var stats = await store.GetStatisticsAsync();
        Assert.Equal(2, stats.HistoryCount);
        Assert.Equal(1, stats.OutboxCount);
        var rows = await store.QueryHistoryAsync(new HistoryQuery(null, null, now.AddDays(-90), now.AddDays(1)));
        Assert.Equal([4d, 3d], rows.Select(s => s.NumericValue!.Value));
    }

    [Fact]
    public async Task ConcurrentCapacityChecksCannotExceedLimit()
    {
        await using var store = new SqliteGatewayStore(DatabasePath);
        await store.InitializeAsync();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => store.EnqueueAsync("topic", i.ToString(), 5)));
        Assert.Equal(5, attempts.Count(x => x));
        Assert.Equal(5, (await store.GetStatisticsAsync()).OutboxCount);
    }

    [Fact]
    public async Task BooleanAndBadQualityHistoryRoundTripWithoutNumericCoercion()
    {
        await using var store = new SqliteGatewayStore(DatabasePath);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var good = MakeSample(now, 1) with { RawValue = true, Value = true };
        var bad = MakeSample(now.AddMilliseconds(1), 1) with { RawValue = null, Value = null, Quality = SampleQuality.Bad, Error = "断线" };
        await store.AppendSamplesAsync([good, bad]);
        var rows = await store.QueryHistoryAsync(new HistoryQuery(null, null, now.AddSeconds(-1), now.AddSeconds(1)));
        Assert.Equal(SampleQuality.Bad, rows[0].Quality);
        Assert.Null(rows[0].Value);
        Assert.IsType<bool>(rows[1].Value);
        Assert.Null(rows[1].NumericValue);
    }

    [Fact]
    public async Task HistoryFilterIsBoundedAndRejectsReversedTime()
    {
        await using var store = new SqliteGatewayStore(DatabasePath);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var a = MakeSample(now, 2);
        var b = MakeSample(now, 3);
        await store.AppendSamplesAsync([a, b]);
        Assert.Single(await store.QueryHistoryAsync(new HistoryQuery(a.DeviceId, null, now.AddSeconds(-1), now.AddSeconds(1))));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.QueryHistoryAsync(new HistoryQuery(null, null, now, now.AddMinutes(-1))));
    }

    private static TelemetrySample MakeSample(DateTimeOffset at, double value) =>
        new(Guid.NewGuid(), "设备", Guid.NewGuid(), "温度", "0", value, value, SampleQuality.Good, at, "°C");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
