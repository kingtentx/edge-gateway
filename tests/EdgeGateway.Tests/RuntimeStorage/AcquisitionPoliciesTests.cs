using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class TelemetryReportPlannerTests
{
    [Fact]
    public void ChangesAccumulateAgainstDurableBaselineAndFailedSelectionDoesNotAdvanceIt()
    {
        var (configuration, device, tag) = CreateConfiguration();
        tag.Deadband = 1;
        var planner = new TelemetryReportPlanner();
        var now = DateTimeOffset.UtcNow;
        TelemetrySample Sample(decimal value) => new(device.Id, device.Name, tag.Id, tag.Name, tag.Address,
            value, value, SampleQuality.Good, now, tag.Unit);
        var initial = Sample(10);
        Assert.Single(planner.Select(configuration, [initial], now));
        // Selecting a snapshot is not a successful store transaction.
        Assert.Single(planner.Select(configuration, [initial], now.AddMilliseconds(100)));
        planner.Commit([initial], now);
        Assert.Empty(planner.Select(configuration, [Sample(10.4m)], now.AddMilliseconds(200)));
        Assert.Empty(planner.Select(configuration, [Sample(10.8m)], now.AddMilliseconds(300)));
        Assert.Single(planner.Select(configuration, [Sample(11.1m)], now.AddMilliseconds(400)));
        Assert.Single(planner.Select(configuration, [Sample(11.1m)], now.AddMilliseconds(500)));
        planner.Commit([Sample(11.1m)], now.AddMilliseconds(500));
        Assert.Empty(planner.Select(configuration, [Sample(11.5m)], now.AddMilliseconds(600)));
    }

    [Fact]
    public void HeartbeatAndQualityTransitionsUseDurableTimeWithoutPretendingTheSourceWasResampled()
    {
        var (configuration, device, tag) = CreateConfiguration();
        configuration.ReportMode = TelemetryReportMode.OnChangeWithHeartbeat;
        configuration.HeartbeatIntervalMs = 1000;
        var planner = new TelemetryReportPlanner();
        var sourceTime = DateTimeOffset.UtcNow;
        var good = new TelemetrySample(device.Id, device.Name, tag.Id, tag.Name, tag.Address,
            42d, 42d, SampleQuality.Good, sourceTime, "");
        var durableTime = sourceTime.AddSeconds(5);
        planner.Commit([good], durableTime);
        Assert.Empty(planner.Select(configuration, [good], durableTime.AddMilliseconds(999)));
        Assert.Same(good, Assert.Single(planner.Select(configuration, [good], durableTime.AddMilliseconds(1000))));
        Assert.Equal(sourceTime, good.Timestamp);
        var stopped = good with { Quality = SampleQuality.Uncertain, Error = "设备已停止" };
        Assert.Single(planner.Select(configuration, [stopped], durableTime.AddMilliseconds(1)));
        planner.Commit([stopped], durableTime.AddMilliseconds(1));
        Assert.Empty(planner.Select(configuration, [stopped], durableTime.AddMilliseconds(2)));
        Assert.Single(planner.Select(configuration, [stopped with { Error = "连接超时" }], durableTime.AddMilliseconds(2)));
        Assert.Single(planner.Select(configuration, [good], durableTime.AddMilliseconds(2)));
        configuration.ReportMode = TelemetryReportMode.Periodic;
        Assert.Single(planner.Select(configuration, [stopped], durableTime.AddMilliseconds(2)));
    }

    [Fact]
    public void TypeAwareComparisonPreservesUInt64TinyNumbersAndStructuredOrderSemantics()
    {
        bool Same(object? first, object? second, double deadband = 0) => TelemetryReportPlanner.ValuesWithinDeadband(
            JsonSerializer.SerializeToElement(first), JsonSerializer.SerializeToElement(second), deadband);
        Assert.False(Same(ulong.MaxValue, ulong.MaxValue - 1));
        Assert.True(Same(ulong.MaxValue, ulong.MaxValue - 1, 1));
        Assert.False(Same(ulong.MaxValue, ulong.MaxValue - 3, 2));
        Assert.False(Same(true, false));
        Assert.False(Same(42, "42"));
        Assert.False(Same(0d, 1e-100));
        Assert.False(Same(1e-100, 2e-100));
        Assert.True(Same(1e-100, 2e-100, 1e-99));
        Assert.True(Same(1e-28, 1.1e-28, 1e-29));
        Assert.True(Same(1.49e-28, 1.51e-28, 3e-30));
        Assert.False(Same(1.49e-28, 1.51e-28, 1e-30));
        Assert.False(Same(-double.MaxValue, double.MaxValue, double.MaxValue));
        Assert.True(Same(decimal.MaxValue, decimal.MinValue, 1e30));
        Assert.False(Same(decimal.MaxValue, decimal.MinValue, 1e28));
        Assert.True(Same(new { one = 1, two = "2" }, new { two = "2", one = 1 }));
        Assert.False(Same(new[] { 1, 2 }, new[] { 2, 1 }));
        Assert.False(Same(new { one = 1 }, new { one = 1, two = 2 }));
        Assert.True(Same(null, null));
    }

    [Fact]
    public void UploadSelectionChecksBothDeviceAndTagIdentityAndPermissions()
    {
        var (configuration, device, tag) = CreateConfiguration();
        var planner = new TelemetryReportPlanner();
        var now = DateTimeOffset.UtcNow;
        var sample = new TelemetrySample(device.Id, device.Name, tag.Id, tag.Name, tag.Address, 2, 2,
            SampleQuality.Good, now, "");
        Assert.Single(planner.Select(configuration, [sample], now));
        Assert.Empty(planner.Select(configuration, [sample with { DeviceId = Guid.NewGuid() }], now));
        tag.UploadEnabled = false;
        Assert.Empty(planner.Select(configuration, [sample], now));
        tag.UploadEnabled = true;
        tag.Enabled = false;
        Assert.Empty(planner.Select(configuration, [sample], now));
        tag.Enabled = true;
        device.Enabled = false;
        Assert.Empty(planner.Select(configuration, [sample], now));
    }

    private static (GatewayConfiguration Configuration, DeviceConfiguration Device, TagConfiguration Tag) CreateConfiguration()
    {
        var tag = new TagConfiguration { Name = "温度", DataType = TagDataType.Float64 };
        var device = new DeviceConfiguration { Tags = [tag] };
        return (new GatewayConfiguration { ReportMode = TelemetryReportMode.OnChange, Devices = [device] }, device, tag);
    }
}

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task SharedChannelSerializesActualReadsAndHonorsTheInterRequestGap()
    {
        await using var harness = new PolicyHarness();
        var channel = new ChannelConfiguration { Name = "共享 TCP 通道", InterRequestDelayMs = 80 };
        harness.Configuration.Channels.Add(channel);
        harness.Configuration.Devices.Add(PolicyDevice("第二台"));
        foreach (var device in harness.Configuration.Devices) device.ChannelId = channel.Id;
        harness.Runtime.Configure(harness.Configuration);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Factory.Completed.Count >= 6, 5000);
        await harness.Runtime.StopAsync();
        var reads = harness.Factory.Completed.Where(read => read.Success).OrderBy(read => read.Started).ToArray();
        Assert.True(reads.Length >= 6);
        Assert.Equal(1, harness.Factory.MaximumConcurrency);
        Assert.Equal(2, reads.Select(read => read.DeviceId).Distinct().Count());
        foreach (var pair in reads.Zip(reads.Skip(1)))
            Assert.True(Stopwatch.GetElapsedTime(pair.First.Completed, pair.Second.Started).TotalMilliseconds >= 75,
                "同通道下一次请求必须在上次完成后等待配置的 80 ms 间隔。");
    }

    [Fact]
    public async Task IndependentChannelsAllowOtherDevicesToReadWhileOneRequestIsBlocked()
    {
        await using var harness = new PolicyHarness();
        harness.Configuration.Devices.Add(PolicyDevice("第二台"));
        foreach (var device in harness.Configuration.Devices)
        {
            var channel = new ChannelConfiguration { Name = device.Name + "通道" };
            harness.Configuration.Channels.Add(channel);
            device.ChannelId = channel.Id;
            device.TimeoutMs = 10000;
        }
        harness.Factory.HoldReads = true;
        harness.Runtime.Configure(harness.Configuration);
        try
        {
            await harness.Runtime.StartAsync();
            await harness.Factory.TwoConcurrentReads.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(harness.Factory.MaximumConcurrency >= 2);
        }
        finally { harness.Factory.ReleaseReads.TrySetResult(true); }
        await Until(() => harness.Runtime.GetLatest().Count(sample => sample.Quality == SampleQuality.Good) == 2);
    }

    [Fact]
    public async Task PointScanRatesIssueDifferentReadSubsetsWithoutCatchupBursts()
    {
        await using var harness = new PolicyHarness();
        var device = harness.Configuration.Devices[0];
        device.ScanMode = DeviceScanMode.TagRate;
        var fast = device.Tags[0];
        fast.ScanIntervalMs = 100;
        var slow = new TagConfiguration { Name = "慢速", Address = "1", DataType = TagDataType.Float64, ScanIntervalMs = 500 };
        device.Tags.Add(slow);
        harness.Runtime.Configure(harness.Configuration);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Factory.ReadsOf(fast.Id) >= 6 && harness.Factory.ReadsOf(slow.Id) >= 2, 5000);
        await harness.Runtime.StopAsync();
        var completed = harness.Factory.Completed.Where(read => read.Success).ToArray();
        Assert.Contains(completed, read => read.TagIds.Length == 1 && read.TagIds[0] == fast.Id);
        Assert.True(harness.Factory.ReadsOf(fast.Id) >= harness.Factory.ReadsOf(slow.Id) * 2);
        var slowReads = completed.Where(read => read.TagIds.Contains(slow.Id)).OrderBy(read => read.Started).ToArray();
        foreach (var pair in slowReads.Zip(slowReads.Skip(1)))
            Assert.True(Stopwatch.GetElapsedTime(pair.First.Completed, pair.Second.Started).TotalMilliseconds >= 480);
    }

    [Fact]
    public async Task DeviceScanRateIgnoresPointOverridesAndPreservesLegacyCycleBehavior()
    {
        await using var harness = new PolicyHarness();
        var device = harness.Configuration.Devices[0];
        device.Tags[0].ScanIntervalMs = 3000;
        device.Tags.Add(new TagConfiguration { Name = "第二点", Address = "1", DataType = TagDataType.Float64, ScanIntervalMs = 6000 });
        harness.Runtime.Configure(harness.Configuration);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Factory.Completed.Count >= 3);
        await harness.Runtime.StopAsync();
        Assert.All(harness.Factory.Completed.Where(read => read.Success), read => Assert.Equal(2, read.TagIds.Length));
    }

    [Fact]
    public async Task FailureDemotionReducesTargetRequestsAndRecoveryClearsItWithoutInterruptingPeers()
    {
        await using var harness = new PolicyHarness();
        var failed = harness.Configuration.Devices[0];
        failed.AutoDemotionEnabled = true;
        failed.DemotionFailureCount = 1;
        failed.DemotionIntervalMs = 600;
        var healthy = PolicyDevice("健康设备");
        harness.Configuration.Devices.Add(healthy);
        harness.Factory.FailingDevices[failed.Id] = true;
        harness.Runtime.Configure(harness.Configuration);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetCommunicationStatistics().Single(status => status.DeviceId == failed.Id).DemotedUntil.HasValue);
        await Until(() => harness.Factory.Completed.Count(read => read.DeviceId == failed.Id) >= 2);
        var failures = harness.Factory.Completed.Where(read => read.DeviceId == failed.Id).OrderBy(read => read.Started).ToArray();
        Assert.True(Stopwatch.GetElapsedTime(failures[0].Completed, failures[1].Started).TotalMilliseconds >= 570);
        Assert.True(harness.Factory.Completed.Count(read => read.DeviceId == healthy.Id && read.Success) >= 3);
        harness.Factory.FailingDevices.TryRemove(failed.Id, out _);
        await Until(() => harness.Runtime.GetLatest().Any(sample => sample.DeviceId == failed.Id && sample.Quality == SampleQuality.Good)
            && harness.Runtime.GetCommunicationStatistics().Single(status => status.DeviceId == failed.Id).DemotedUntil is null);
        var recovered = harness.Runtime.GetDeviceStatuses().Single(status => status.DeviceId == failed.Id);
        Assert.Equal(DeviceState.Running, recovered.State);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.True(harness.Runtime.GetCommunicationStatistics().Single(status => status.DeviceId == failed.Id).FailedRequests >= 2);
        Assert.True(harness.Runtime.IsDeviceRunning(healthy.Id));
    }

    [Fact]
    public async Task OnChangePublishesRestartedDeviceFirstSampleEvenWhenItsValueAndConfigurationAreUnchanged()
    {
        await using var harness = new PolicyHarness();
        var first = harness.Configuration.Devices[0];
        var second = PolicyDevice("重启目标");
        harness.Configuration.Devices.Add(second);
        harness.Configuration.Mqtt.Enabled = true;
        harness.Configuration.ReportMode = TelemetryReportMode.OnChange;
        harness.Configuration.PublishIntervalMs = 100;
        harness.Store.BlockFirstAcceptedEnqueue = true;
        harness.Runtime.Configure(harness.Configuration);
        try
        {
            await harness.Runtime.StartAsync();
            await harness.Store.EnqueueBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstMessage = Assert.Single(harness.Store.Accepted);
            using (var initial = JsonDocument.Parse(firstMessage))
                Assert.Equal(2, initial.RootElement.GetProperty("samples").GetArrayLength());
            // The old Good snapshot has reached storage, but QueueTelemetry has not yet committed its baseline.
            await harness.Runtime.StopDeviceAsync(second.Id);
            await harness.Runtime.StartDeviceAsync(second.Id);
            await Until(() => harness.Runtime.GetLatest().Any(sample => sample.DeviceId == second.Id && sample.Quality == SampleQuality.Good));
            Assert.True(harness.Runtime.IsDeviceRunning(first.Id));
            Assert.Equal(1, harness.Factory.Creations[first.Id]);
            Assert.Equal(2, harness.Factory.Creations[second.Id]);
        }
        finally { harness.Store.ReleaseEnqueue.TrySetResult(true); }
        await Until(() => harness.Store.Accepted.Count >= 2, 5000);
        using var restarted = JsonDocument.Parse(harness.Store.Accepted.Skip(1).First());
        var sample = Assert.Single(restarted.RootElement.GetProperty("samples").EnumerateArray());
        Assert.Equal(second.Id, sample.GetProperty("deviceId").GetGuid());
        Assert.Equal("Good", sample.GetProperty("quality").GetString());
        Assert.Equal(2, sample.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task FailedOnChangeEnqueueRetriesSameMessageBeforeAdvancingTheBaseline()
    {
        await using var harness = new PolicyHarness();
        harness.Configuration.Mqtt.Enabled = true;
        harness.Configuration.ReportMode = TelemetryReportMode.OnChange;
        harness.Configuration.PublishIntervalMs = 100;
        harness.Store.FailuresRemaining = 1;
        harness.Runtime.Configure(harness.Configuration);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetTelemetryStatus().PersistenceError is not null);
        Assert.Empty(harness.Store.Accepted);
        await Until(() => harness.Store.Accepted.Count == 1, 5000);
        Assert.True(harness.Store.Attempts.Count >= 2);
        Assert.Equal(harness.Store.Attempts.First(), harness.Store.Attempts.Skip(1).First());
        Assert.Equal(harness.Store.Attempts.First(), harness.Store.Accepted.First());
        var reads = harness.Factory.Completed.Count;
        await Until(() => harness.Factory.Completed.Count >= reads + 3);
        Assert.Single(harness.Store.Accepted); // Unchanged values stop producing rows only after durable success.
        Assert.Null(harness.Runtime.GetTelemetryStatus().PersistenceError);
    }

    private static DeviceConfiguration PolicyDevice(string name) => new()
    {
        Name = name, DriverId = "policy-test", PollIntervalMs = 100, TimeoutMs = 1000,
        Tags = [new TagConfiguration { Name = "温度", Address = "0", DataType = TagDataType.Float64 }]
    };

    private sealed class PolicyHarness : IAsyncDisposable
    {
        public GatewayConfiguration Configuration { get; } = new() { Devices = [PolicyDevice("第一台")] };
        public PolicyFactory Factory { get; } = new();
        public PolicyStore Store { get; } = new();
        public FakePublisher Publisher { get; } = new();
        public GatewayRuntime Runtime { get; }
        public PolicyHarness() => Runtime = new(Configuration, new DriverRegistry([Factory], new GatewayLog()), Store, Publisher, new GatewayLog());
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed record PolicyRead(Guid DeviceId, Guid[] TagIds, long Started, long Completed, bool Success);

    private sealed class PolicyFactory : IDriverFactory
    {
        public DriverDescriptor Descriptor { get; } = new("policy-test", "调度测试驱动", "实际运行时互斥与调度验证", false);
        public ConcurrentQueue<PolicyRead> Completed { get; } = new();
        public ConcurrentDictionary<Guid, int> Creations { get; } = new();
        public ConcurrentDictionary<Guid, bool> FailingDevices { get; } = new();
        public bool HoldReads;
        public int MaximumConcurrency;
        private int _active;
        public TaskCompletionSource<bool> ReleaseReads { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> TwoConcurrentReads { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadsOf(Guid tagId) => Completed.Count(read => read.Success && read.TagIds.Contains(tagId));
        public IReadOnlyList<string> Validate(DeviceConfiguration configuration) => [];
        public IDeviceDriver Create(DeviceConfiguration configuration)
        {
            Creations.AddOrUpdate(configuration.Id, 1, (_, count) => count + 1);
            return new PolicyDriver(this, configuration.Id);
        }

        private sealed class PolicyDriver(PolicyFactory owner, Guid deviceId) : IDeviceDriver
        {
            public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken)
            {
                var started = Stopwatch.GetTimestamp();
                var active = Interlocked.Increment(ref owner._active);
                int observed;
                do { observed = Volatile.Read(ref owner.MaximumConcurrency); }
                while (active > observed && Interlocked.CompareExchange(ref owner.MaximumConcurrency, active, observed) != observed);
                if (active >= 2) owner.TwoConcurrentReads.TrySetResult(true);
                var success = false;
                try
                {
                    if (owner.HoldReads) await owner.ReleaseReads.Task.WaitAsync(cancellationToken);
                    await Task.Delay(20, cancellationToken);
                    if (owner.FailingDevices.ContainsKey(deviceId)) throw new IOException("测试通信失败");
                    success = true;
                    return tags.Select(tag => new TagReadResult(tag.Id, 2d)).ToArray();
                }
                finally
                {
                    owner.Completed.Enqueue(new(deviceId, tags.Select(tag => tag.Id).ToArray(), started, Stopwatch.GetTimestamp(), success));
                    Interlocked.Decrement(ref owner._active);
                }
            }
            public Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken) => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class PolicyStore : IGatewayStore
    {
        private readonly FakeStore _inner = new();
        private int _blocked;
        public bool BlockFirstAcceptedEnqueue;
        public int FailuresRemaining;
        public ConcurrentQueue<string> Attempts { get; } = new();
        public ConcurrentQueue<string> Accepted { get; } = new();
        public TaskCompletionSource<bool> EnqueueBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseEnqueue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);
        public Task AppendSamplesAsync(IReadOnlyList<TelemetrySample> samples, CancellationToken cancellationToken = default) => _inner.AppendSamplesAsync(samples, cancellationToken);
        public Task<IReadOnlyList<TelemetrySample>> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default) => _inner.QueryHistoryAsync(query, cancellationToken);
        public async Task<bool> EnqueueAsync(string topic, string payload, int maxMessages, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(payload);
            if (Interlocked.Decrement(ref FailuresRemaining) >= 0) throw new IOException("测试持久化失败");
            var accepted = await _inner.EnqueueAsync(topic, payload, maxMessages, cancellationToken);
            if (accepted)
            {
                Accepted.Enqueue(payload);
                if (BlockFirstAcceptedEnqueue && Interlocked.Exchange(ref _blocked, 1) == 0)
                {
                    EnqueueBlocked.TrySetResult(true);
                    await ReleaseEnqueue.Task.WaitAsync(cancellationToken);
                }
            }
            return accepted;
        }
        public Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(int limit, CancellationToken cancellationToken = default) => _inner.ReadOutboxAsync(limit, cancellationToken);
        public Task AcknowledgeAsync(long id, CancellationToken cancellationToken = default) => _inner.AcknowledgeAsync(id, cancellationToken);
        public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) => _inner.GetStatisticsAsync(cancellationToken);
        public Task PruneHistoryAsync(int retentionDays, int maxRows, CancellationToken cancellationToken = default) => _inner.PruneHistoryAsync(retentionDays, maxRows, cancellationToken);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
