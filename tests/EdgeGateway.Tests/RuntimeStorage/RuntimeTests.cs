using System.Collections.Concurrent;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class RuntimeTests
{
    [Fact]
    public async Task AppliesEngineeringScaleAndDoesNotObserveOutsideConfigurationMutations()
    {
        await using var harness = new Harness();
        harness.Config.Devices[0].Tags[0].Scale = 2;
        harness.Config.Devices[0].Tags[0].Offset = 10;
        harness.Runtime.Configure(harness.Config);
        harness.Config.Devices[0].Tags[0].Scale = 999;
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Good));
        var sample = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(2d, Convert.ToDouble(sample.RawValue));
        Assert.Equal(14d, sample.NumericValue);
        Assert.Throws<InvalidOperationException>(() => harness.Runtime.Configure(harness.Config));
    }

    [Fact]
    public async Task FailedReadClearsValueAndChangesQuality()
    {
        await using var harness = new Harness();
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Good));
        harness.Factory.FailReads = true;
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Bad));
        var sample = Assert.Single(harness.Runtime.GetLatest());
        Assert.Null(sample.Value);
        Assert.Null(sample.RawValue);
        Assert.Equal(DeviceState.Reconnecting, Assert.Single(harness.Runtime.GetDeviceStatuses()).State);
    }

    [Fact]
    public async Task NonfiniteValuesBecomeBadAndRemainJsonSafe()
    {
        await using var harness = new Harness();
        harness.Factory.Value = double.PositiveInfinity;
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count > 0);
        var sample = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(SampleQuality.Bad, sample.Quality);
        Assert.Null(sample.RawValue);
        Assert.Null(sample.Value);
        Assert.Contains("NaN", sample.Error);
    }

    [Fact]
    public async Task WriteInvertsEngineeringScaleAndIsSerializedWithReads()
    {
        await using var harness = new Harness();
        var tag = harness.Config.Devices[0].Tags[0];
        tag.Writable = true;
        tag.Scale = 2;
        tag.Offset = 10;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Good));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => harness.Runtime.WriteAsync(harness.Config.Devices[0].Id, tag.Id, "20")));
        Assert.Equal(5d, Convert.ToDouble(harness.Factory.LastWrite));
        Assert.Equal(1, harness.Factory.MaxConcurrentOperations);
    }

    [Fact]
    public async Task WritesRequirePermissionAndRejectFractionalIntegerValues()
    {
        await using var harness = new Harness();
        await harness.Runtime.StartAsync();
        var device = harness.Config.Devices[0];
        var tag = device.Tags[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runtime.WriteAsync(device.Id, tag.Id, "5"));
        await harness.Runtime.StopAsync();
        tag.Writable = true;
        tag.DataType = TagDataType.UInt16;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Good));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Runtime.WriteAsync(device.Id, tag.Id, "1.5"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Runtime.WriteAsync(device.Id, tag.Id, "70000"));
        Assert.Null(harness.Factory.LastWrite);
    }

    [Fact]
    public async Task IntegerWriteAllowsOnlyNarrowFloatingPointRoundoff()
    {
        await using var harness = new Harness();
        var device = harness.Config.Devices[0];
        var tag = device.Tags[0];
        tag.Writable = true;
        tag.DataType = TagDataType.Int16;
        tag.Scale = 0.1;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Good));
        await harness.Runtime.WriteAsync(device.Id, tag.Id, "0.3");
        Assert.Equal((short)3, Assert.IsType<short>(harness.Factory.LastWrite));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Runtime.WriteAsync(device.Id, tag.Id, "0.300001"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Runtime.WriteAsync(device.Id, tag.Id, "0.00000000001"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Runtime.WriteAsync(device.Id, tag.Id, "3276.8"));
    }

    [Fact]
    public async Task StopDrainsHistoryDisposesDriverAndAllowsRestart()
    {
        await using var harness = new Harness();
        await harness.Runtime.StartAsync();
        await Until(() => harness.Store.Samples.Count >= 2);
        await harness.Runtime.StopAsync();
        Assert.False(harness.Runtime.IsRunning);
        Assert.True(harness.Factory.Disposals >= 1);
        Assert.All(harness.Runtime.GetLatest(), s => Assert.Equal(SampleQuality.Uncertain, s.Quality));
        var count = harness.Store.Samples.Count;
        await Task.Delay(150);
        Assert.Equal(count, harness.Store.Samples.Count);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Store.Samples.Count > count);
        Assert.True(harness.Runtime.IsRunning);
    }

    [Fact]
    public async Task FailedPublisherKeepsDurableRowsAndRetriesAfterRecovery()
    {
        await using var harness = new Harness();
        harness.Config.Mqtt.Enabled = true;
        harness.Config.PublishIntervalMs = 100;
        harness.Publisher.Fail = true;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Store.Outbox.Count > 0 && harness.Publisher.Attempts > 0);
        var first = harness.Store.Outbox.First().Value;
        Assert.DoesNotContain(first.Id, harness.Store.Acknowledged);
        harness.Publisher.Fail = false;
        await Until(() => harness.Store.Acknowledged.Contains(first.Id), 5000);
        Assert.Contains(first.Payload, harness.Publisher.Payloads);
    }

    [Fact]
    public void ValidatorRejectsSharedSerialPortsAndDuplicateTagIds()
    {
        var factory = new FakeFactory("modbus-rtu");
        var registry = new DriverRegistry([factory], new GatewayLog());
        var tag = new TagConfiguration { Name = "点位", Address = "0" };
        var config = new GatewayConfiguration
        {
            Devices = [new DeviceConfiguration { DriverId = "modbus-rtu", SerialPortName = "COM1", Tags = [tag] },
                new DeviceConfiguration { DriverId = "modbus-rtu", SerialPortName = "com1", Tags = [new TagConfiguration { Id = tag.Id }] }]
        };
        var errors = ConfigurationValidator.Validate(config, registry);
        Assert.Contains(errors, e => e.Contains("同一串口"));
        Assert.Contains(errors, e => e.Contains("标识为空或重复"));
    }

    private static async Task Until(Func<bool> condition, int milliseconds = 3000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("未在期限内观察到预期状态。");
            await Task.Delay(20);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public GatewayConfiguration Config { get; } = new()
        {
            Devices = [new DeviceConfiguration { DriverId = "test", Name = "测试设备", PollIntervalMs = 100, TimeoutMs = 1000,
                Tags = [new TagConfiguration { Name = "温度", Address = "0", DataType = TagDataType.Float64 }] }]
        };
        public FakeFactory Factory { get; } = new();
        public FakeStore Store { get; } = new();
        public FakePublisher Publisher { get; } = new();
        public GatewayRuntime Runtime { get; }
        public Harness() => Runtime = new GatewayRuntime(Config, new DriverRegistry([Factory], new GatewayLog()), Store, Publisher, new GatewayLog());
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class FakeFactory(string id = "test") : IDriverFactory
    {
        public DriverDescriptor Descriptor => new(id, "测试驱动", "test", true);
        public volatile bool FailReads;
        public double Value = 2;
        public object? LastWrite;
        public int MaxConcurrentOperations;
        public int Disposals;
        private int _active;
        public IDeviceDriver Create(DeviceConfiguration configuration) => new FakeDriver(this);
        public IReadOnlyList<string> Validate(DeviceConfiguration configuration) => [];
        private sealed class FakeDriver(FakeFactory owner) : IDeviceDriver
        {
            public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken)
            {
                var active = Interlocked.Increment(ref owner._active);
                owner.MaxConcurrentOperations = Math.Max(owner.MaxConcurrentOperations, active);
                try
                {
                    await Task.Delay(10, cancellationToken);
                    if (owner.FailReads) throw new IOException("设备断线");
                    return tags.Select(t => new TagReadResult(t.Id, owner.Value)).ToArray();
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public async Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken)
            {
                var active = Interlocked.Increment(ref owner._active);
                owner.MaxConcurrentOperations = Math.Max(owner.MaxConcurrentOperations, active);
                try { await Task.Delay(10, cancellationToken); owner.LastWrite = value; }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposals); return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakePublisher : ITelemetryPublisher
    {
        public volatile bool Fail;
        public bool IsConnected => !Fail;
        public int Attempts;
        public ConcurrentBag<string> Payloads { get; } = [];
        public Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            if (Fail) throw new IOException("broker unavailable");
            Payloads.Add(payload);
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStore : IGatewayStore
    {
        public ConcurrentQueue<TelemetrySample> Samples { get; } = [];
        public ConcurrentDictionary<long, OutboxMessage> Outbox { get; } = new();
        public ConcurrentBag<long> Acknowledged { get; } = [];
        private long _id;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendSamplesAsync(IReadOnlyList<TelemetrySample> samples, CancellationToken cancellationToken = default)
        { foreach (var sample in samples) Samples.Enqueue(sample); return Task.CompletedTask; }
        public Task<IReadOnlyList<TelemetrySample>> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TelemetrySample>>(Samples.ToArray());
        public Task<bool> EnqueueAsync(string topic, string payload, int maxMessages, CancellationToken cancellationToken = default)
        {
            if (Outbox.Count >= maxMessages) return Task.FromResult(false);
            var id = Interlocked.Increment(ref _id);
            Outbox[id] = new OutboxMessage(id, topic, payload, DateTimeOffset.UtcNow);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Outbox.OrderBy(kv => kv.Key).Take(limit).Select(kv => kv.Value).ToArray());
        public Task AcknowledgeAsync(long id, CancellationToken cancellationToken = default)
        { Outbox.TryRemove(id, out _); Acknowledged.Add(id); return Task.CompletedTask; }
        public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoreStatistics(Samples.Count, Outbox.Count));
        public Task PruneHistoryAsync(int retentionDays, int maxRows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
