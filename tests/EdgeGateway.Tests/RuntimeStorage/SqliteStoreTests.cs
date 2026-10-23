using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using Microsoft.Data.Sqlite;
using System.Text.Json;

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
    public async Task VersionOneDatabaseAddsDurableRpcJournalWithoutChangingHistoryOrPendingMessages()
    {
        Directory.CreateDirectory(_directory);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
        var sample = MakeSample(DateTimeOffset.UtcNow, 42) with { RawValue = ulong.MaxValue, Value = ulong.MaxValue };
        const string payload = "{\"messageId\":\"v1-pending\",\"value\":18446744073709551615}";
        await using (var legacy = new SqliteConnection(connectionString))
        {
            await legacy.OpenAsync();
            await using var command = legacy.CreateCommand();
            command.CommandText = """
                CREATE TABLE samples (id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT NOT NULL,
                    tag_id TEXT NOT NULL, timestamp_ms INTEGER NOT NULL, sample_json TEXT NOT NULL);
                CREATE TABLE outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, topic TEXT NOT NULL,
                    payload TEXT NOT NULL, created_ms INTEGER NOT NULL);
                INSERT INTO samples VALUES(7,$device,$tag,$time,$sample);
                INSERT INTO outbox VALUES(41,'legacy/telemetry',$payload,$time);
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue("$device", sample.DeviceId.ToString("D"));
            command.Parameters.AddWithValue("$tag", sample.TagId.ToString("D"));
            command.Parameters.AddWithValue("$time", sample.Timestamp.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$sample", JsonSerializer.Serialize(sample));
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync();
        }
        await using (var migrated = new SqliteGatewayStore(DatabasePath))
        {
            await migrated.InitializeAsync();
            var history = Assert.Single(await migrated.QueryHistoryAsync(new HistoryQuery(sample.DeviceId, sample.TagId,
                sample.Timestamp.AddSeconds(-1), sample.Timestamp.AddSeconds(1))));
            Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(history.Value));
            var queued = Assert.Single(await migrated.ReadOutboxAsync(10));
            Assert.Equal(41L, queued.Id);
            Assert.Equal("legacy/telemetry", queued.Topic);
            Assert.Equal(payload, queued.Payload);
            var claimed = await migrated.ClaimRpcCommandAsync("migration-command", "fingerprint", 10);
            Assert.True(claimed.Created);
            await migrated.CompleteRpcCommandAsync("migration-command", "reply/topic", "{\"state\":\"Matched\"}");
        }
        await using (var reopened = new SqliteGatewayStore(DatabasePath))
        {
            await reopened.InitializeAsync();
            var replay = await reopened.ClaimRpcCommandAsync("migration-command", "fingerprint", 10);
            Assert.False(replay.Created);
            Assert.Equal("{\"state\":\"Matched\"}", replay.Entry.ResponsePayload);
            Assert.Equal(payload, Assert.Single(await reopened.ReadOutboxAsync(10)).Payload);
        }
        await using var check = new SqliteConnection(connectionString);
        await check.OpenAsync();
        await using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, (long)(await version.ExecuteScalarAsync())!);
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

    [Fact]
    public async Task WideIntegersTextAndStructuredValuesRoundTripWithoutDoubleCoercion()
    {
        await using var store = new SqliteGatewayStore(DatabasePath);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var values = new object[] { ulong.MaxValue, long.MinValue, "  工件 A  ",
            JsonSerializer.SerializeToElement(new { counter = ulong.MaxValue, coordinates = new[] { 1.25, 2.5 } }) };
        var samples = values.Select((value, index) => MakeSample(now.AddMilliseconds(index), 0) with { RawValue = value, Value = value }).ToArray();
        await store.AppendSamplesAsync(samples);
        var rows = await store.QueryHistoryAsync(new HistoryQuery(null, null, now.AddSeconds(-1), now.AddSeconds(1)));
        var ordered = rows.OrderBy(row => row.Timestamp).ToArray();
        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(ordered[0].Value));
        Assert.Equal(long.MinValue, Assert.IsType<long>(ordered[1].Value));
        Assert.Equal("  工件 A  ", Assert.IsType<string>(ordered[2].Value));
        var structured = Assert.IsType<JsonElement>(ordered[3].Value);
        Assert.Equal(ulong.MaxValue, structured.GetProperty("counter").GetUInt64());
        Assert.Equal(2.5, structured.GetProperty("coordinates")[1].GetDouble());
    }

    private static TelemetrySample MakeSample(DateTimeOffset at, double value) =>
        new(Guid.NewGuid(), "设备", Guid.NewGuid(), "温度", "0", value, value, SampleQuality.Good, at, "°C");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
