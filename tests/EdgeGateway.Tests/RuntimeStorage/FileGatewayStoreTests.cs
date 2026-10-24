using EdgeGateway.DataAccess;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class FileGatewayStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edge-state-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OutboxSurvivesRestartUntilAcknowledgedWithoutCreatingSqlite()
    {
        long id;
        DateTimeOffset created;
        await using (var first = new FileGatewayStore(_directory))
        {
            await first.InitializeAsync();
            Assert.True(await first.EnqueueAsync("edge/telemetry", "{\"messageId\":\"stable-1\"}", 5));
            var row = Assert.Single(await first.ReadOutboxAsync(10));
            id = row.Id; created = row.CreatedAt;
        }
        await using var reopened = new FileGatewayStore(_directory);
        await reopened.InitializeAsync();
        var message = Assert.Single(await reopened.ReadOutboxAsync(10));
        Assert.Equal(id, message.Id);
        Assert.Contains("stable-1", message.Payload);
        Assert.Equal(created, (await reopened.GetStatisticsAsync()).OldestOutboxAt);
        await reopened.AcknowledgeAsync(id);
        Assert.Empty(await reopened.ReadOutboxAsync(10));
        Assert.Null((await reopened.GetStatisticsAsync()).OldestOutboxAt);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.db", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConcurrentCapacityChecksPreserveExistingMessagesAndFifo()
    {
        await using var store = new FileGatewayStore(_directory);
        await store.InitializeAsync();
        var accepted = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => store.EnqueueAsync("topic", i.ToString(), 5)));
        Assert.Equal(5, accepted.Count(value => value));
        var rows = await store.ReadOutboxAsync(10);
        Assert.Equal(5, rows.Count);
        Assert.Equal(rows.OrderBy(row => row.Id).Select(row => row.Id), rows.Select(row => row.Id));
        await store.AcknowledgeAsync(rows[0].Id);
        Assert.Equal(rows[1].CreatedAt, (await store.GetStatisticsAsync()).OldestOutboxAt);
        Assert.True(await store.EnqueueAsync("topic", "new", 5));
        Assert.Equal("new", (await store.ReadOutboxAsync(10))[^1].Payload);
    }

    [Fact]
    public async Task RpcClaimAndCompletedResultAreDurableAndCannotBeOverwritten()
    {
        await using (var first = new FileGatewayStore(_directory))
        {
            await first.InitializeAsync();
            Assert.True((await first.ClaimRpcCommandAsync("pending", "fingerprint-a", 5)).Created);
            Assert.True((await first.ClaimRpcCommandAsync("completed", "fingerprint-b", 5)).Created);
            await first.CompleteRpcCommandAsync("completed", "reply/topic", "{\"state\":\"Matched\"}");
        }
        await using var reopened = new FileGatewayStore(_directory);
        await reopened.InitializeAsync();
        var pending = await reopened.ClaimRpcCommandAsync("pending", "different", 5);
        Assert.False(pending.Created);
        Assert.Equal("fingerprint-a", pending.Entry.Fingerprint);
        Assert.Null(pending.Entry.ResponsePayload);
        var completed = await reopened.ClaimRpcCommandAsync("completed", "fingerprint-b", 5);
        Assert.False(completed.Created);
        Assert.Equal("{\"state\":\"Matched\"}", completed.Entry.ResponsePayload);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CompleteRpcCommandAsync("completed", "reply/topic", "overwrite"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ClaimRpcCommandAsync("new", "fingerprint", 2));
    }

    [Fact]
    public async Task CancellationDoesNotAdmitOutboxOrWriteClaim()
    {
        await using var store = new FileGatewayStore(_directory);
        await store.InitializeAsync();
        var token = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnqueueAsync("topic", "cancelled", 5, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ClaimRpcCommandAsync("cancelled", "fingerprint", 5, token));
        Assert.Empty(await store.ReadOutboxAsync(5));
        Assert.Null(await store.GetRpcCommandAsync("cancelled"));
    }

    [Fact]
    public async Task CorruptOutboxIsReportedWithoutDroppingTheFile()
    {
        await using (var first = new FileGatewayStore(_directory))
        {
            await first.InitializeAsync();
            await first.EnqueueAsync("topic", "payload", 5);
        }
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "outbox"), "*.json"));
        await File.WriteAllTextAsync(path, "{broken");
        await using var reopened = new FileGatewayStore(_directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.InitializeAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CorruptRpcClaimCannotBeTreatedAsANewCommand()
    {
        await using (var first = new FileGatewayStore(_directory))
        {
            await first.InitializeAsync();
            await first.ClaimRpcCommandAsync("important-write", "fingerprint", 5);
        }
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "rpc"), "*.json"));
        await File.WriteAllTextAsync(path, "{broken");
        await using var reopened = new FileGatewayStore(_directory);
        await reopened.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ClaimRpcCommandAsync("important-write", "fingerprint", 5));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SecondProcessStoreCannotOwnTheSameDirectory()
    {
        await using var first = new FileGatewayStore(_directory);
        await first.InitializeAsync();
        await using var second = new FileGatewayStore(_directory);
        await Assert.ThrowsAnyAsync<IOException>(() => second.InitializeAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
