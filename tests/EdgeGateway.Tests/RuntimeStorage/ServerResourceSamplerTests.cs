using System.Threading.Channels;
using EdgeGateway.Abstractions;
using EdgeGateway.Hosting;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class ServerResourceSamplerTests
{
    [Fact]
    public async Task SamplingContinuesWithoutClientReadsAndReturnsMissingRefreshHistory()
    {
        var monitor = new QueuedMonitor();
        await using var sampler = new ServerResourceSampler(monitor, TimeSpan.FromMilliseconds(5));
        var start = DateTimeOffset.UtcNow;
        monitor.Push(Snapshot(start, 10));
        var first = await sampler.ReadAsync();
        Assert.Single(first.History);
        for (var second = 1; second <= 6; second++) monitor.Push(Snapshot(start.AddSeconds(second), 10 + second));
        var later = await WaitFor(sampler, snapshot => snapshot.History.Count == 7);
        Assert.Equal(Enumerable.Range(0, 7).Select(second => start.AddSeconds(second)), later.History.Select(reading => reading.Timestamp));
        Assert.All(later.History, reading => Assert.NotNull(reading.CpuUsagePercent));
        Assert.Single(first.History); // Earlier snapshots are immutable.
        var calls = monitor.Completed;
        await sampler.ReadAsync(); await sampler.ReadAsync();
        Assert.Equal(calls, monitor.Completed); // IPC reads consume cached measurements rather than advancing CPU counters.
    }

    [Fact]
    public async Task HistoryIsBoundedAndClockRollbackStartsNewWindow()
    {
        var monitor = new QueuedMonitor();
        await using var sampler = new ServerResourceSampler(monitor, TimeSpan.FromMilliseconds(1));
        var start = DateTimeOffset.UtcNow;
        for (var second = 0; second <= 90; second++) monitor.Push(Snapshot(start.AddSeconds(second), second));
        var result = await WaitFor(sampler, snapshot => snapshot.Timestamp == start.AddSeconds(90));
        Assert.Equal(61, result.History.Count);
        Assert.Equal(start.AddSeconds(30), result.History[0].Timestamp);
        monitor.Push(Snapshot(start.AddSeconds(-1), 5));
        var rollback = await WaitFor(sampler, snapshot => snapshot.Timestamp == start.AddSeconds(-1));
        Assert.Single(rollback.History);
    }

    [Fact]
    public async Task InvalidReadingsRemainMissingAndSubsequentSamplesRecover()
    {
        var monitor = new QueuedMonitor();
        await using var sampler = new ServerResourceSampler(monitor, TimeSpan.FromMilliseconds(2));
        var start = DateTimeOffset.UtcNow;
        monitor.Push(Snapshot(start, 50));
        await sampler.ReadAsync();
        monitor.Push(new(start.AddSeconds(1), "fixture", 8, null, null, [], ["Unavailable"]));
        var failed = await WaitFor(sampler, snapshot => snapshot.History.Count == 2);
        Assert.Null(failed.History[^1].CpuUsagePercent);
        Assert.Null(failed.History[^1].MemoryUsagePercent);
        monitor.Push(Snapshot(start.AddSeconds(2), 20));
        var recovered = await WaitFor(sampler, snapshot => snapshot.History.Count == 3);
        Assert.Equal(20, recovered.CpuUsagePercent);
        Assert.Null(recovered.History[1].CpuUsagePercent);
    }

    [Fact]
    public async Task SamplingExceptionCreatesMissingReadingAndLoopRecovers()
    {
        var monitor = new QueuedMonitor();
        await using var sampler = new ServerResourceSampler(monitor, TimeSpan.FromMilliseconds(2));
        monitor.PushFailure(new IOException("temporary failure"));
        var failed = await sampler.ReadAsync();
        Assert.Null(failed.CpuUsagePercent);
        Assert.Contains(failed.Errors, error => error.Contains("temporary failure"));
        monitor.Push(Snapshot(failed.Timestamp.AddSeconds(1), 33));
        var recovered = await WaitFor(sampler, snapshot => snapshot.History.Count == 2);
        Assert.Null(recovered.History[0].CpuUsagePercent);
        Assert.Equal(33, recovered.History[1].CpuUsagePercent);
    }

    [Fact]
    public async Task CancellingOneReaderDoesNotStopWorkerSamplingAndDisposalCancelsInFlightRead()
    {
        var monitor = new QueuedMonitor();
        var sampler = new ServerResourceSampler(monitor, TimeSpan.FromMilliseconds(2));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampler.ReadAsync(new(true)));
            monitor.Push(Snapshot(DateTimeOffset.UtcNow, 12));
            Assert.Equal(12, (await sampler.ReadAsync()).CpuUsagePercent);
        }
        finally { await sampler.DisposeAsync(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sampler.ReadAsync());
    }

    private static ServerResourceSnapshot Snapshot(DateTimeOffset timestamp, double cpu) =>
        new(timestamp, "fixture", 8, cpu, new(1000, 400), [], []);

    private static async Task<ServerResourceSnapshot> WaitFor(ServerResourceSampler sampler, Func<ServerResourceSnapshot, bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var result = await sampler.ReadAsync(timeout.Token);
            if (ready(result)) return result;
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class QueuedMonitor : IServerResourceMonitor
    {
        private readonly Channel<object> _readings = Channel.CreateUnbounded<object>();
        private int _completed;
        public int Completed => Volatile.Read(ref _completed);
        public void Push(ServerResourceSnapshot snapshot) => _readings.Writer.TryWrite(snapshot);
        public void PushFailure(Exception exception) => _readings.Writer.TryWrite(exception);
        public async Task<ServerResourceSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = await _readings.Reader.ReadAsync(cancellationToken);
            Interlocked.Increment(ref _completed);
            if (snapshot is Exception exception) throw exception;
            return (ServerResourceSnapshot)snapshot;
        }
    }
}
