using System.Collections.Concurrent;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed partial class RuntimeTests
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

    public static IEnumerable<object[]> ExactValues()
    {
        yield return [TagDataType.UInt64, ulong.MaxValue];
        yield return [TagDataType.Int64, long.MinValue];
        yield return [TagDataType.AsciiString, "  part A  "];
        yield return [TagDataType.Utf8String, "工件甲"];
        yield return [TagDataType.DateTime, new DateTime(2026, 10, 22, 12, 34, 56, DateTimeKind.Utc)];
        yield return [TagDataType.Float32, JsonSerializer.SerializeToElement(new[] { 1.25f, 2.5f })];
        yield return [TagDataType.Any, JsonSerializer.SerializeToElement(new { count = ulong.MaxValue, state = "ready" })];
    }

    [Theory]
    [MemberData(nameof(ExactValues))]
    public async Task ExpandedValueTypesKeepExactValuesAndBatchShape(TagDataType type, object raw)
    {
        await using var harness = new Harness();
        harness.Factory.Value = raw;
        harness.Config.Devices[0].Tags[0].DataType = type;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count > 0);
        var sample = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(SampleQuality.Good, sample.Quality);
        Assert.NotNull(sample.Value);
        Assert.Equal(raw.GetType(), sample.Value.GetType());
        Assert.Equal(JsonSerializer.Serialize(raw), JsonSerializer.Serialize(sample.Value));
    }

    [Fact]
    public async Task BatchValuesRejectScalarEngineeringConversionInsteadOfInventingNumbers()
    {
        await using var harness = new Harness();
        harness.Factory.Value = JsonSerializer.SerializeToElement(new[] { 1.25f, 2.5f });
        harness.Config.Devices[0].Tags[0].DataType = TagDataType.Float32;
        harness.Config.Devices[0].Tags[0].Scale = 2;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count > 0);
        var sample = Assert.Single(harness.Runtime.GetLatest());
        Assert.Equal(SampleQuality.Bad, sample.Quality);
        Assert.Null(sample.Value);
        Assert.Contains("结构化", sample.Error);
    }

    [Fact]
    public async Task UInt64AndTextWritesPreserveValuesIncludingWhitespaceAndEmptyText()
    {
        await using var harness = new Harness();
        var device = harness.Config.Devices[0];
        var tag = device.Tags[0];
        tag.Writable = true;
        tag.DataType = TagDataType.UInt64;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Good));
        await harness.Runtime.WriteAsync(device.Id, tag.Id, "18446744073709551615");
        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(harness.Factory.LastWrite));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Runtime.WriteAsync(device.Id, tag.Id, "18446744073709551616"));
        await harness.Runtime.StopAsync();
        tag.DataType = TagDataType.Utf8String;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Good));
        await harness.Runtime.WriteAsync(device.Id, tag.Id, "  工件 A  ");
        Assert.Equal("  工件 A  ", harness.Factory.LastWrite);
        await harness.Runtime.WriteAsync(device.Id, tag.Id, "");
        Assert.Equal("", harness.Factory.LastWrite);
    }

    [Fact]
    public async Task BitWritesApplyInverseEngineeringScaleAndRemainWithinZeroOrOne()
    {
        await using var harness = new Harness();
        var device = harness.Config.Devices[0];
        var tag = device.Tags[0];
        tag.Writable = true;
        tag.DataType = TagDataType.Bit;
        tag.Scale = 2;
        tag.Offset = 10;
        harness.Factory.Value = 0;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Good));
        await harness.Runtime.WriteAsync(device.Id, tag.Id, "12");
        Assert.Equal(1, Assert.IsType<int>(harness.Factory.LastWrite));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Runtime.WriteAsync(device.Id, tag.Id, "11"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Runtime.WriteAsync(device.Id, tag.Id, "14"));
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
    public async Task IndividualStartAndStopPreserveOtherDevicesHistoryAndMqtt()
    {
        await using var harness = new Harness();
        var first = harness.Config.Devices[0];
        first.Tags[0].Writable = true;
        var second = AddSecondDevice(harness);
        harness.Config.Mqtt.Enabled = true;
        harness.Config.PublishIntervalMs = 100;
        harness.Runtime.Configure(harness.Config);

        await harness.Runtime.StartDeviceAsync(first.Id);
        await Until(() => harness.Runtime.GetLatest().Any(s => s.DeviceId == first.Id && s.Quality == SampleQuality.Good));
        Assert.True(harness.Runtime.IsDeviceRunning(first.Id));
        Assert.False(harness.Runtime.IsDeviceRunning(second.Id));
        Assert.Equal(DeviceState.Stopped, harness.Runtime.GetDeviceStatuses().Single(s => s.DeviceId == second.Id).State);
        Assert.False(harness.Factory.CreationsByDevice.ContainsKey(second.Id));

        await harness.Runtime.StartDeviceAsync(second.Id);
        await Until(() => harness.Runtime.GetLatest().Count(s => s.Quality == SampleQuality.Good) == 2 && harness.Publisher.Attempts > 0);
        Assert.Equal(1, harness.Store.Initializations);
        await harness.Runtime.StopDeviceAsync(first.Id);
        Assert.False(harness.Runtime.IsDeviceRunning(first.Id));
        Assert.True(harness.Runtime.IsDeviceRunning(second.Id));
        Assert.True(harness.Runtime.IsRunning);
        Assert.Equal(0, harness.Publisher.Disconnections);
        Assert.Throws<InvalidOperationException>(() => harness.Runtime.Configure(harness.Config));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runtime.WriteAsync(first.Id, first.Tags[0].Id, "5"));

        var stopped = harness.Runtime.GetLatest().Single(s => s.DeviceId == first.Id);
        Assert.Equal(SampleQuality.Uncertain, stopped.Quality);
        var firstReads = harness.Factory.ReadsByDevice[first.Id];
        var secondReads = harness.Factory.ReadsByDevice[second.Id];
        var publications = harness.Publisher.Attempts;
        await Until(() => harness.Factory.ReadsByDevice[second.Id] > secondReads && harness.Publisher.Attempts > publications);
        Assert.Equal(firstReads, harness.Factory.ReadsByDevice[first.Id]);
        Assert.Equal(stopped, harness.Runtime.GetLatest().Single(s => s.DeviceId == first.Id));
        Assert.Equal(SampleQuality.Good, harness.Runtime.GetLatest().Single(s => s.DeviceId == second.Id).Quality);
        Assert.Contains(harness.Store.Samples, s => s.DeviceId == first.Id);
        Assert.Contains(harness.Store.Samples, s => s.DeviceId == second.Id);
        Assert.Equal(1, harness.Factory.CreationsByDevice[second.Id]);
        Assert.False(harness.Factory.DisposalsByDevice.ContainsKey(second.Id));

        await harness.Runtime.StopDeviceAsync(second.Id);
        Assert.False(harness.Runtime.IsRunning);
        Assert.Equal(1, harness.Publisher.Disconnections);
        Assert.All(harness.Runtime.GetLatest(), s => Assert.Equal(SampleQuality.Uncertain, s.Quality));
        var stored = harness.Store.Samples.Count;
        await Task.Delay(150);
        Assert.Equal(stored, harness.Store.Samples.Count);
        Assert.All(harness.Factory.CreationsByDevice, pair => Assert.Equal(pair.Value, harness.Factory.DisposalsByDevice[pair.Key]));

        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartDeviceAsync(first.Id);
        await Until(() => harness.Store.Samples.Count > stored);
        Assert.Equal(2, harness.Store.Initializations);
        Assert.Equal(2, harness.Factory.CreationsByDevice[first.Id]);
        Assert.Equal(1, harness.Factory.CreationsByDevice[second.Id]);
        Assert.False(harness.Runtime.IsDeviceRunning(second.Id));
    }

    [Fact]
    public async Task RepeatedAndConcurrentLifecycleCommandsDoNotDuplicateDeviceSessions()
    {
        await using var harness = new Harness();
        var first = harness.Config.Devices[0];
        var second = AddSecondDevice(harness);
        harness.Runtime.Configure(harness.Config);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => harness.Runtime.StartDeviceAsync(first.Id)));
        await Until(() => harness.Factory.ReadsByDevice.ContainsKey(first.Id));
        Assert.Equal(1, harness.Factory.CreationsByDevice[first.Id]);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Factory.ReadsByDevice.ContainsKey(second.Id));
        Assert.Equal(1, harness.Factory.CreationsByDevice[first.Id]);
        Assert.Equal(1, harness.Factory.CreationsByDevice[second.Id]);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => harness.Runtime.StopDeviceAsync(first.Id)));
        Assert.Equal(1, harness.Factory.DisposalsByDevice[first.Id]);
        Assert.True(harness.Runtime.IsDeviceRunning(second.Id));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => harness.Runtime.StartDeviceAsync(first.Id)));
        await Until(() => harness.Factory.CreationsByDevice[first.Id] == 2);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 2 == 0
            ? harness.Runtime.StopDeviceAsync(first.Id) : harness.Runtime.StartDeviceAsync(first.Id)));
        await harness.Runtime.StopAsync();
        Assert.False(harness.Runtime.IsRunning);
        Assert.False(harness.Runtime.IsDeviceRunning(first.Id));
        Assert.False(harness.Runtime.IsDeviceRunning(second.Id));
        Assert.All(harness.Factory.MaximumLiveDriversByDevice.Values, count => Assert.Equal(1, count));
        Assert.All(harness.Factory.CreationsByDevice, pair => Assert.Equal(pair.Value, harness.Factory.DisposalsByDevice[pair.Key]));
    }

    [Fact]
    public async Task DisabledUnknownAndCancelledStartsDoNotLaunchWorkers()
    {
        await using var harness = new Harness();
        var first = harness.Config.Devices[0];
        var disabled = AddSecondDevice(harness);
        disabled.Enabled = false;
        harness.Runtime.Configure(harness.Config);
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Runtime.StartDeviceAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Runtime.StopDeviceAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runtime.StartDeviceAsync(disabled.Id));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Runtime.StartDeviceAsync(first.Id, cancelled.Token));
        Assert.False(harness.Runtime.IsRunning);
        Assert.Empty(harness.Factory.CreationsByDevice);
        Assert.Equal(0, harness.Store.Initializations);

        using var startup = new CancellationTokenSource();
        await harness.Runtime.StartDeviceAsync(first.Id, startup.Token);
        startup.Cancel();
        await Until(() => harness.Runtime.GetLatest().Any(s => s.Quality == SampleQuality.Good));
        Assert.True(harness.Runtime.IsDeviceRunning(first.Id));
        Assert.False(harness.Runtime.IsDeviceRunning(disabled.Id));
    }

    [Fact]
    public async Task FailedDeviceCanBeStoppedWithoutInterruptingHealthyDevice()
    {
        await using var harness = new Harness();
        var first = harness.Config.Devices[0];
        var second = AddSecondDevice(harness);
        harness.Factory.FailingDevices[first.Id] = true;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetDeviceStatuses().Any(s => s.DeviceId == first.Id && s.State == DeviceState.Reconnecting)
            && harness.Runtime.GetLatest().Any(s => s.DeviceId == first.Id && s.Quality == SampleQuality.Bad)
            && harness.Factory.ReadsByDevice.GetValueOrDefault(second.Id) >= 2);
        Assert.True(harness.Runtime.IsDeviceRunning(first.Id));
        Assert.Equal(SampleQuality.Bad, harness.Runtime.GetLatest().Single(s => s.DeviceId == first.Id).Quality);
        await harness.Runtime.StopDeviceAsync(first.Id);
        var secondReads = harness.Factory.ReadsByDevice[second.Id];
        await Until(() => harness.Factory.ReadsByDevice[second.Id] > secondReads);
        Assert.True(harness.Runtime.IsRunning);
        Assert.True(harness.Runtime.IsDeviceRunning(second.Id));
        Assert.Equal(SampleQuality.Good, harness.Runtime.GetLatest().Single(s => s.DeviceId == second.Id).Quality);
        Assert.Equal(1, harness.Factory.CreationsByDevice[second.Id]);
    }

    [Fact]
    public async Task StoppingDeviceCancelsItsInFlightWriteAndLeavesOtherDeviceCollecting()
    {
        await using var harness = new Harness();
        var first = harness.Config.Devices[0];
        first.Tags[0].Writable = true;
        first.TimeoutMs = 10000; // Stop must cancel this write before its operation timeout.
        var second = AddSecondDevice(harness);
        harness.Factory.BlockingWriteDevice = first.Id;
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count(s => s.Quality == SampleQuality.Good) == 2);
        var write = harness.Runtime.WriteAsync(first.Id, first.Tags[0].Id, "5");
        await harness.Factory.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await harness.Runtime.StopDeviceAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.False(harness.Runtime.IsDeviceRunning(first.Id));
        Assert.True(harness.Runtime.IsDeviceRunning(second.Id));
        Assert.Null(harness.Factory.LastWrite);
        Assert.Equal(1, harness.Factory.DisposalsByDevice[first.Id]);
        var reads = harness.Factory.ReadsByDevice[second.Id];
        await Until(() => harness.Factory.ReadsByDevice[second.Id] > reads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runtime.WriteAsync(first.Id, first.Tags[0].Id, "6"));
    }

    private static DeviceConfiguration AddSecondDevice(Harness harness)
    {
        var second = new DeviceConfiguration
        {
            DriverId = "test", Name = "第二台测试设备", PollIntervalMs = 100, TimeoutMs = 1000,
            Tags = [new TagConfiguration { Name = "压力", Address = "1", DataType = TagDataType.Float64 }]
        };
        harness.Config.Devices.Add(second);
        return second;
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
        public object Value = 2d;
        public object? LastWrite;
        public int MaxConcurrentOperations;
        public int Disposals;
        public Guid? BlockingWriteDevice;
        public TaskCompletionSource<bool> WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<Guid, bool> FailingDevices { get; } = new();
        public ConcurrentDictionary<string, bool> FailingHosts { get; } = new();
        public ConcurrentDictionary<Guid, int> CreationsByDevice { get; } = new();
        public ConcurrentDictionary<Guid, int> DisposalsByDevice { get; } = new();
        public ConcurrentDictionary<Guid, int> ReadsByDevice { get; } = new();
        public ConcurrentDictionary<Guid, int> LiveDriversByDevice { get; } = new();
        public ConcurrentDictionary<Guid, int> MaximumLiveDriversByDevice { get; } = new();
        private int _active;
        public IDeviceDriver Create(DeviceConfiguration configuration)
        {
            CreationsByDevice.AddOrUpdate(configuration.Id, 1, (_, count) => count + 1);
            var live = LiveDriversByDevice.AddOrUpdate(configuration.Id, 1, (_, count) => count + 1);
            MaximumLiveDriversByDevice.AddOrUpdate(configuration.Id, live, (_, count) => Math.Max(count, live));
            return new FakeDriver(this, configuration.Id, configuration.Host);
        }
        public IReadOnlyList<string> Validate(DeviceConfiguration configuration) => [];
        private sealed class FakeDriver(FakeFactory owner, Guid deviceId, string host) : IDeviceDriver
        {
            public Task ConnectAsync(CancellationToken cancellationToken) => owner.FailingHosts.ContainsKey(host)
                ? Task.FromException(new IOException("测试连接失败：" + host)) : Task.CompletedTask;
            public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken)
            {
                var active = Interlocked.Increment(ref owner._active);
                owner.MaxConcurrentOperations = Math.Max(owner.MaxConcurrentOperations, active);
                try
                {
                    await Task.Delay(10, cancellationToken);
                    if (owner.FailReads || owner.FailingDevices.ContainsKey(deviceId)) throw new IOException("设备断线");
                    owner.ReadsByDevice.AddOrUpdate(deviceId, 1, (_, count) => count + 1);
                    return tags.Select(t => new TagReadResult(t.Id, owner.Value)).ToArray();
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public async Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken)
            {
                var active = Interlocked.Increment(ref owner._active);
                owner.MaxConcurrentOperations = Math.Max(owner.MaxConcurrentOperations, active);
                try
                {
                    if (deviceId == owner.BlockingWriteDevice)
                    {
                        owner.WriteStarted.TrySetResult(true);
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    await Task.Delay(10, cancellationToken);
                    owner.LastWrite = value;
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Disposals);
                owner.DisposalsByDevice.AddOrUpdate(deviceId, 1, (_, count) => count + 1);
                owner.LiveDriversByDevice.AddOrUpdate(deviceId, 0, (_, count) => count - 1);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakePublisher : ITelemetryPublisher
    {
        public volatile bool Fail;
        public bool IsConnected => !Fail;
        public int Attempts;
        public int Disconnections;
        public ConcurrentBag<string> Payloads { get; } = [];
        public Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            if (Fail) throw new IOException("broker unavailable");
            Payloads.Add(payload);
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Disconnections); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStore : IGatewayStore
    {
        public ConcurrentQueue<TelemetrySample> Samples { get; } = [];
        public ConcurrentDictionary<long, OutboxMessage> Outbox { get; } = new();
        public ConcurrentBag<long> Acknowledged { get; } = [];
        private long _id;
        public int Initializations;
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Initializations); return Task.CompletedTask; }
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
