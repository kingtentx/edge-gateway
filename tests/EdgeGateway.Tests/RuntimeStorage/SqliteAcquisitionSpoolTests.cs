using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class SqliteAcquisitionSpoolTests
{
    private static DatabaseWriteRow Row(string id, object value, DateTimeOffset timestamp) =>
        new(id, timestamp, new Dictionary<string, object?> { ["event_id"] = id, ["value"] = value, ["timestamp"] = timestamp, ["quality"] = 192 });

    [Fact]
    public async Task PendingBatchSurvivesRestartAndRetainsTypedValuesUntilAcknowledged()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-acquisition-spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "acquisition-spool.db");
        var group = Guid.NewGuid();
        var timestamp = DateTimeOffset.Parse("2026-10-26T10:11:12.1234567+00:00");
        try
        {
            string batchId;
            await using (var spool = new SqliteAcquisitionSpool(path, maxBytes: 1024 * 1024, maxRows: 10))
            {
                batchId = await spool.AppendAsync(group, [
                    Row("event-1", 18446744073709551615UL, timestamp),
                    Row("event-2", 123.456m, timestamp)
                ]);
                var stats = await spool.GetStatisticsAsync();
                Assert.Equal(1, stats.PendingBatches);
                Assert.Equal(2, stats.PendingRows);
                Assert.True(stats.PendingBytes > 0);
            }

            await using var reopened = new SqliteAcquisitionSpool(path, maxBytes: 1024 * 1024, maxRows: 10);
            var batch = Assert.Single(await reopened.ReadPendingAsync(group));
            Assert.Equal(batchId, batch.BatchId);
            Assert.Equal(timestamp, batch.Rows[0].Values["timestamp"]);
            Assert.Equal(ulong.MaxValue, batch.Rows[0].Values["value"]);
            Assert.Equal(123.456m, batch.Rows[1].Values["value"]);
            await reopened.AcknowledgeAsync(group, batchId);
            var empty = await reopened.GetStatisticsAsync();
            Assert.Equal(0, empty.PendingBatches);
            Assert.Equal(0, empty.PendingRows);
            Assert.Equal(1, empty.AcknowledgedBatches);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CapacityRejectsNewBatchWithoutEvictingPreviouslyDurableRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-acquisition-spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var spool = new SqliteAcquisitionSpool(Path.Combine(root, "spool.db"), maxBytes: 1024 * 1024, maxRows: 1);
            var group = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var batchId = await spool.AppendAsync(group, [Row("keep", "durable", timestamp)]);
            await Assert.ThrowsAsync<IOException>(() => spool.AppendAsync(group, [Row("reject", "not persisted", timestamp)]));
            var pending = Assert.Single(await spool.ReadPendingAsync(group));
            Assert.Equal(batchId, pending.BatchId);
            Assert.Equal("keep", pending.Rows[0].EventId);
            var stats = await spool.GetStatisticsAsync();
            Assert.Equal(1, stats.PendingRows);
            Assert.Equal(1, stats.CapacityRejectedBatches);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SeparateGroupsHaveIndependentReplayQueues()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-acquisition-spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var spool = new SqliteAcquisitionSpool(Path.Combine(root, "spool.db"));
            var first = Guid.NewGuid(); var second = Guid.NewGuid(); var timestamp = DateTimeOffset.UtcNow;
            await spool.AppendAsync(first, [Row("a", 1, timestamp)]);
            await spool.AppendAsync(second, [Row("b", 2, timestamp)]);
            Assert.Equal("a", Assert.Single(await spool.ReadPendingAsync(first)).Single().Rows.Single().EventId);
            Assert.Equal("b", Assert.Single(await spool.ReadPendingAsync(second)).Single().Rows.Single().EventId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
