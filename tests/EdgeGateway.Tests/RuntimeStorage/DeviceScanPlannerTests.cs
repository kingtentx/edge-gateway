using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

/// <summary>Deterministic elapsed-time tests; these do not sleep or claim real-device throughput.</summary>
public sealed class DeviceScanPlannerTests
{
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(100, 1000)]
    [InlineData(200, 1000)]
    [InlineData(999, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 2000)]
    [InlineData(1500, 2000)]
    [InlineData(2000, 2000)]
    [InlineData(2450, 3000)]
    public void CompletionStaysOnThePlannedGridAndSkipsOnlyPastDeadlines(long completedAt, long nextDue)
    {
        var device = Device();
        var planner = new DeviceScanPlanner(device);
        var first = planner.GetDue(0);
        Assert.Single(first);
        planner.Complete(first, completedAt);

        Assert.Equal((int)(nextDue - completedAt), planner.DelayUntilNext(completedAt));
        if (nextDue > completedAt) Assert.Empty(planner.GetDue(nextDue - 1));
        Assert.Equal(device.Tags[0].Id, Assert.Single(planner.GetDue(nextDue)).Id);
    }

    [Fact]
    public void VariableReadDurationsDoNotAccumulateDriftOverALogicalDay()
    {
        var planner = new DeviceScanPlanner(Device());
        int[] durations = [200, 50, 750, 1, 999, 400];
        for (var cycle = 0; cycle < 86_400; cycle++)
        {
            var plannedStart = cycle * 1000L;
            var due = planner.GetDue(plannedStart);
            Assert.Single(due);
            var completedAt = plannedStart + durations[cycle % durations.Length];
            planner.Complete(due, completedAt);
            var expectedNext = plannedStart + 1000;
            Assert.Equal(expectedNext, completedAt + planner.DelayUntilNext(completedAt));
            Assert.Empty(planner.GetDue(expectedNext - 1));
        }
        Assert.Single(planner.GetDue(86_400_000));
    }

    [Fact]
    public void DelayedFirstReadKeepsTheSessionOriginInsteadOfRebasingToCompletion()
    {
        var planner = new DeviceScanPlanner(Device());
        var first = planner.GetDue(2300);
        Assert.Single(first);
        planner.Complete(first, 2500);
        Assert.Equal(500, planner.DelayUntilNext(2500));
        Assert.Empty(planner.GetDue(2999));
        Assert.Single(planner.GetDue(3000));
    }

    [Fact]
    public void LateWakeOrReconnectCoalescesMissedPeriodsIntoOneCurrentRead()
    {
        var planner = new DeviceScanPlanner(Device());
        planner.Complete(planner.GetDue(0), 200);
        // The device resumes after several missed deadlines. Read once, not seven times.
        var resumed = planner.GetDue(7300);
        Assert.Single(resumed);
        planner.Complete(resumed, 7450);
        Assert.Equal(550, planner.DelayUntilNext(7450));
        Assert.Empty(planner.GetDue(7450));
        Assert.Empty(planner.GetDue(7999));
        var next = planner.GetDue(8000);
        Assert.Single(next);
        planner.Complete(next, 8200);
        Assert.Equal(800, planner.DelayUntilNext(8200));
    }

    [Theory]
    [InlineData(600, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(2300, 3000)]
    public void OutputHandoffTimeIsIncludedBeforeChoosingTheNextDeadline(long handoffCompletedAt, long nextDue)
    {
        var planner = new DeviceScanPlanner(Device());
        var due = planner.GetDue(0);
        // Device I/O finished at 200 ms; the bounded output channel held this batch longer.
        // Runtime must call Complete after WriteAsync, using the post-handoff timestamp.
        planner.Complete(due, handoffCompletedAt);
        Assert.Equal(nextDue, handoffCompletedAt + planner.DelayUntilNext(handoffCompletedAt));
        if (nextDue > handoffCompletedAt) Assert.Empty(planner.GetDue(handoffCompletedAt));
    }

    [Fact]
    public void PointRatesAndInheritedRatesKeepIndependentDeadlinesAndSkipDisabledTags()
    {
        var fast = new TagConfiguration { Name = "fast", ScanIntervalMs = 100 };
        var slow = new TagConfiguration { Name = "slow", ScanIntervalMs = 500 };
        var inherited = new TagConfiguration { Name = "inherited", ScanIntervalMs = 0 };
        var disabled = new TagConfiguration { Name = "disabled", Enabled = false, ScanIntervalMs = 100 };
        var planner = new DeviceScanPlanner(new DeviceConfiguration
        {
            PollIntervalMs = 1000, ScanMode = DeviceScanMode.TagRate,
            Tags = [fast, slow, inherited, disabled]
        });
        var initial = planner.GetDue(0);
        Assert.Equal(new[] { fast.Id, slow.Id, inherited.Id }, initial.Select(tag => tag.Id));
        planner.Complete(initial, 20);
        Assert.Equal(80, planner.DelayUntilNext(20));
        Assert.Empty(planner.GetDue(99));
        var fastRead = planner.GetDue(100);
        Assert.Equal(fast.Id, Assert.Single(fastRead).Id);
        planner.Complete(fastRead, 550); // The slow tag became due while the fast read was blocked.
        Assert.Equal(0, planner.DelayUntilNext(550));
        var slowRead = planner.GetDue(550);
        Assert.Equal(slow.Id, Assert.Single(slowRead).Id);
        planner.Complete(slowRead, 560);
        Assert.Equal(40, planner.DelayUntilNext(560));
        Assert.Equal(fast.Id, Assert.Single(planner.GetDue(600)).Id);
        Assert.Equal(new[] { fast.Id, slow.Id, inherited.Id }, planner.GetDue(1000).Select(tag => tag.Id));
    }

    [Fact]
    public void DeviceRateIgnoresPointOverrides()
    {
        var device = Device();
        device.Tags[0].ScanIntervalMs = 100;
        device.Tags.Add(new TagConfiguration { Name = "other", ScanIntervalMs = 3000 });
        var planner = new DeviceScanPlanner(device);
        var due = planner.GetDue(0);
        Assert.Equal(2, due.Count);
        planner.Complete(due, 200);
        Assert.Equal(800, planner.DelayUntilNext(200));
        Assert.Empty(planner.GetDue(999));
        Assert.Equal(2, planner.GetDue(1000).Count);
    }

    [Fact]
    public void NoEnabledTagsUseTheDeviceIntervalWithoutBusySpinning()
    {
        var device = Device();
        device.Tags[0].Enabled = false;
        var planner = new DeviceScanPlanner(device);
        Assert.Empty(planner.GetDue(0));
        planner.Complete([], 200);
        Assert.Equal(1000, planner.DelayUntilNext(200));
        Assert.Empty(planner.GetDue(1000));
    }

    [Fact]
    public void RestartHasItsOwnImmediateFirstScan()
    {
        var device = Device();
        var oldSession = new DeviceScanPlanner(device);
        oldSession.Complete(oldSession.GetDue(0), 200);
        Assert.Empty(oldSession.GetDue(200));
        var restarted = new DeviceScanPlanner(device);
        Assert.Equal(0, restarted.DelayUntilNext(0));
        Assert.Single(restarted.GetDue(0));
    }

    [Fact]
    public void LongElapsedTimeDoesNotWrapAtInt32Milliseconds()
    {
        var planner = new DeviceScanPlanner(Device());
        planner.Complete(planner.GetDue(0), 200);
        const long completedAt = 4_320_000_123; // A logical 50-day runtime, not a wall-clock test.
        planner.Complete(planner.GetDue(completedAt), completedAt);
        Assert.Equal(877, planner.DelayUntilNext(completedAt));
        Assert.Empty(planner.GetDue(4_320_000_999));
        Assert.Single(planner.GetDue(4_320_001_000));
    }

    [Fact]
    public void InvalidZeroPeriodCannotTurnCompletionIntoAnUnboundedCatchupLoop()
    {
        var device = Device();
        device.PollIntervalMs = 0;
        var planner = new DeviceScanPlanner(device);
        Assert.Throws<ArgumentOutOfRangeException>(() => planner.Complete(planner.GetDue(0), 200));
    }

    private static DeviceConfiguration Device() => new()
    {
        PollIntervalMs = 1000, ScanMode = DeviceScanMode.DeviceRate,
        Tags = [new TagConfiguration { Name = "value" }]
    };
}
