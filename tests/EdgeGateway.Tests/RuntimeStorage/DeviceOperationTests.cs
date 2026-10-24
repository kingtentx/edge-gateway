using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.IotGateway;
using EdgeGateway.Model;
using EdgeGateway.Service;
using EdgeGateway.Tests.IotGateway;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class DeviceOperationTests
{
    [Fact]
    public async Task StoppedProbeConnectsReadsAndDisposesWithoutStartingCollectionOrHistory()
    {
        await using var h = new OpHarness(c => { c.Devices[0].Tags[0].Scale = 2; c.Devices[0].Tags[0].Offset = 10; });
        var before = DateTimeOffset.UtcNow;
        var result = await h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id);
        Assert.Equal(DeviceProbeState.Succeeded, result.State);
        Assert.False(result.UsedRunningSession);
        Assert.True(result.Connected);
        Assert.Equal(14d, result.Sample!.Value);
        Assert.Equal(2d, result.Sample.RawValue);
        Assert.InRange(result.Sample.Timestamp, before, result.CompletedAt);
        Assert.Equal(h.Tag.Id, result.TagId);
        Assert.Equal(1, h.State.Connections);
        Assert.Equal(1, h.State.Reads);
        Assert.Equal(1, h.State.Disposals);
        Assert.Equal(0, h.State.LiveDrivers);
        Assert.Equal(0, h.Store.Initializations);
        Assert.Empty(h.Runtime.GetLatest());
        Assert.False(h.Runtime.IsRunning);
        Assert.Equal(DeviceState.Stopped, Assert.Single(h.Runtime.GetDeviceStatuses()).State);
    }

    [Fact]
    public async Task ConnectionOnlyProbeHasNoInventedGoodPoint()
    {
        await using var h = new OpHarness(c => c.Devices[0].Tags.Clear());
        var result = await h.Runtime.ProbeDeviceAsync(h.Device.Id);
        Assert.Equal(DeviceProbeState.ConnectedOnly, result.State);
        Assert.True(result.Success);
        Assert.Equal(SampleQuality.Uncertain, result.Quality);
        Assert.Null(result.Sample);
        Assert.Null(result.TagId);
        Assert.Equal(1, h.State.Connections);
        Assert.Equal(0, h.State.Reads);
        Assert.Equal(1, h.State.Disposals);
    }

    [Fact]
    public async Task RunningProbeUsesOneFreshReadWithoutReplacingSessionOrCachedSample()
    {
        await using var h = new OpHarness();
        await h.StartReadyAsync();
        var cached = Assert.Single(h.Runtime.GetLatest());
        h.State.Value = 7d;
        var result = await h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id, new DeviceProbeOptions { RequireRunning = true });
        Assert.Equal(DeviceProbeState.Succeeded, result.State);
        Assert.True(result.UsedRunningSession);
        Assert.Equal(7d, result.Sample!.Value);
        Assert.Equal(cached, Assert.Single(h.Runtime.GetLatest()));
        Assert.Equal(1, h.State.Creations);
        Assert.Equal(2, h.State.Reads);
        Assert.Equal(0, h.State.Disposals);
    }

    [Fact]
    public async Task RequireRunningProbeAndDisabledPointRefuseBeforeDriverCreation()
    {
        await using var h = new OpHarness();
        var stopped = await h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id, new DeviceProbeOptions { RequireRunning = true });
        Assert.Equal(DeviceProbeState.Failed, stopped.State);
        Assert.Contains("运行状态", stopped.Error);
        h.Tag.Enabled = false;
        h.Runtime.Configure(h.Config);
        var disabled = await h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id);
        Assert.Equal(DeviceProbeState.Failed, disabled.State);
        Assert.Equal(0, h.State.Creations);
        Assert.Equal(0, h.Store.Initializations);
    }

    [Fact]
    public async Task DisabledSerialProbeCannotBypassAnotherEnabledDevicesPortOwnership()
    {
        await using var h = new OpHarness(c =>
        {
            c.Devices[0].DriverId = "modbus-rtu";
            c.Devices[0].Enabled = false;
            c.Devices.Add(new DeviceConfiguration { Name = "owner", DriverId = "modbus-rtu", SerialPortName = "com1" });
        }, "modbus-rtu");
        var result = await h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id);
        Assert.Equal(DeviceProbeState.Failed, result.State);
        Assert.Contains("同一串口", result.Error);
        Assert.Equal(0, h.State.Creations);
        Assert.False(h.Config.Devices[0].Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedProbeTimeoutOrCancellationClearsValuesAndCleansConnection(bool cancel)
    {
        await using var h = new OpHarness();
        h.State.ReadRelease = NewSignal();
        using var cancellation = new CancellationTokenSource();
        var pending = h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id,
            new DeviceProbeOptions { TimeoutMs = cancel ? 1000 : 50 }, cancellation.Token);
        await h.State.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancel) cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(cancel ? DeviceProbeState.Cancelled : DeviceProbeState.TimedOut, result.State);
        Assert.False(result.Success);
        Assert.Null(result.Sample!.Value);
        Assert.Null(result.Sample.RawValue);
        Assert.NotEqual(SampleQuality.Good, result.Quality);
        Assert.Equal(1, h.State.Disposals);
        Assert.Equal(0, h.State.DisposedDuringOperation);
        Assert.False(h.Runtime.IsRunning);
    }

    [Fact]
    public async Task FailedConnectionIsExplicitAndStillDisposed()
    {
        await using var h = new OpHarness();
        h.State.FailConnect = true;
        var result = await h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id);
        Assert.Equal(DeviceProbeState.Failed, result.State);
        Assert.Equal(SampleQuality.Bad, result.Quality);
        Assert.False(result.Connected);
        Assert.Contains("connect failed", result.Error);
        Assert.Equal(0, h.State.Reads);
        Assert.Equal(1, h.State.Disposals);
    }

    [Fact]
    public async Task StartingDuringStoppedProbeWaitsForTemporaryConnectionToBeReleased()
    {
        await using var h = new OpHarness();
        var release = h.State.ReadRelease = NewSignal();
        var probe = h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id);
        await h.State.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var start = h.Runtime.StartDeviceAsync(h.Device.Id);
        Assert.False(start.IsCompleted);
        Assert.Equal(0, h.Store.Initializations);
        release.TrySetResult();
        Assert.Equal(DeviceProbeState.Succeeded, (await probe).State);
        await start;
        await Until(() => h.Runtime.GetLatest().Count == 1);
        Assert.Equal(2, h.State.Creations);
        Assert.Equal(1, h.State.Disposals);
        Assert.Equal(1, h.State.MaximumLiveDrivers);
        Assert.Equal(0, h.State.DisposedDuringOperation);
    }

    [Fact]
    public async Task VerifiedWriteUsesFreshReadbackEngineeringConversionAndAuditableCommandId()
    {
        await using var h = new OpHarness(c => { c.Devices[0].Tags[0].Scale = 2; c.Devices[0].Tags[0].Offset = 10; });
        await h.StartReadyAsync();
        var id = Guid.NewGuid();
        var result = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "20", new WriteVerificationOptions { CommandId = id, Source = "test-local" });
        Assert.Equal(WriteCommandState.Matched, result.State);
        Assert.Equal(id, result.CommandId);
        Assert.True(result.WriteAttempted);
        Assert.True(result.Submitted);
        Assert.True(result.VerificationRequested);
        Assert.Equal(5d, result.WrittenRawValue);
        Assert.Equal(20d, result.ExpectedEngineeringValue);
        Assert.Equal(20d, result.ReadBack!.Value);
        Assert.Equal(1, result.ReadBackAttempts);
        Assert.InRange(result.ReadBack.Timestamp, result.StartedAt, result.CompletedAt);
        Assert.Equal(1, h.State.Writes);
        Assert.Contains(h.Log.GetEntries(), entry => entry.Message.Contains(id.ToString()) && entry.Message.Contains("test-local") && entry.Message.Contains("Matched"));
        Assert.Equal(result.ReadBack, Assert.Single(h.Runtime.GetLatest()));
    }

    [Fact]
    public async Task CachedMatchingGoodSampleNeverConfirmsAnUnchangedDeviceWrite()
    {
        await using var h = new OpHarness();
        await h.StartReadyAsync();
        Assert.Equal(2d, Assert.Single(h.Runtime.GetLatest()).Value);
        h.State.AfterWriteValue = _ => 99d;
        var result = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "2",
            new WriteVerificationOptions { MaxAttempts = 3, RetryIntervalMs = 0 });
        Assert.Equal(WriteCommandState.Mismatched, result.State);
        Assert.True(result.Submitted);
        Assert.Equal(99d, result.ReadBack!.Value);
        Assert.Equal(3, result.ReadBackAttempts);
        Assert.Equal(3, h.State.ReadsAfterWrite);
        Assert.Equal(1, h.State.Writes);
    }

    [Fact]
    public async Task DelayedReadbackRetriesReadsOnlyAndStopsAtFirstMatch()
    {
        await using var h = new OpHarness();
        await h.StartReadyAsync();
        h.State.AfterWriteValue = attempt => attempt < 3 ? 1d : 42d;
        var result = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "42",
            new WriteVerificationOptions { MaxAttempts = 5, RetryIntervalMs = 0 });
        Assert.Equal(WriteCommandState.Matched, result.State);
        Assert.Equal(3, result.ReadBackAttempts);
        Assert.Equal(1, h.State.Writes);
        Assert.Equal(1, h.State.MaximumConcurrentOperations);
    }

    public static IEnumerable<object[]> ExactIntegerCases()
    {
        yield return [TagDataType.UInt64, "18446744073709551615", ulong.MaxValue, true];
        yield return [TagDataType.UInt64, "18446744073709551615", ulong.MaxValue - 1, false];
        yield return [TagDataType.UInt64, "9007199254740993", 9007199254740992UL, false];
        yield return [TagDataType.Int64, "-9223372036854775808", long.MinValue, true];
        yield return [TagDataType.Int64, "-9223372036854775808", long.MinValue + 1, false];
    }

    [Theory]
    [MemberData(nameof(ExactIntegerCases))]
    public async Task WideIntegerVerificationDoesNotLoseSingleUnitPrecision(TagDataType type, string input, object readback, bool matches)
    {
        await using var h = new OpHarness(c => c.Devices[0].Tags[0].DataType = type);
        h.State.Value = readback;
        await h.StartReadyAsync();
        h.State.AfterWriteValue = _ => readback;
        var result = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, input, new WriteVerificationOptions { MaxAttempts = 1 });
        Assert.Equal(matches ? WriteCommandState.Matched : WriteCommandState.Mismatched, result.State);
        Assert.Equal(type == TagDataType.UInt64 ? typeof(ulong) : typeof(long), result.WrittenRawValue!.GetType());
        Assert.Equal(input, Convert.ToString(result.WrittenRawValue, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(TagDataType.Boolean, "1", true)]
    [InlineData(TagDataType.Boolean, "false", false)]
    [InlineData(TagDataType.Utf8String, "  工件 A  ", "  工件 A  ")]
    [InlineData(TagDataType.Utf8String, "", "")]
    public async Task BooleanAndTextVerificationPreservesTypeAndWhitespace(TagDataType type, string input, object expected)
    {
        await using var h = new OpHarness(c => c.Devices[0].Tags[0].DataType = type);
        h.State.Value = expected;
        await h.StartReadyAsync();
        var result = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, input);
        Assert.Equal(WriteCommandState.Matched, result.State);
        Assert.Equal(expected, result.WrittenRawValue);
        Assert.Equal(expected, result.ReadBack!.Value);
    }

    [Theory]
    [InlineData(0.05, 0, 100.04, true)]
    [InlineData(0, 0.01, 100.9, true)]
    [InlineData(0.01, 0.001, 100.2, false)]
    [InlineData(0, 0, 100.00000001, false)]
    public async Task NumericTolerancesAreExplicitAndBounded(double absolute, double relative, double readback, bool matches)
    {
        await using var h = new OpHarness();
        await h.StartReadyAsync();
        h.State.AfterWriteValue = _ => readback;
        var result = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "100", new WriteVerificationOptions
        { AbsoluteTolerance = absolute, RelativeTolerance = relative, MaxAttempts = 1 });
        Assert.Equal(matches ? WriteCommandState.Matched : WriteCommandState.Mismatched, result.State);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task InvalidReadbackQualityOrShapeCannotConfirmAWrite(string fault)
    {
        await using var h = new OpHarness();
        await h.StartReadyAsync();
        h.State.ReadbackFault = fault;
        var result = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "2", new WriteVerificationOptions { MaxAttempts = 2, RetryIntervalMs = 0 });
        Assert.Equal(WriteCommandState.Failed, result.State);
        Assert.True(result.Submitted);
        Assert.Equal(SampleQuality.Bad, result.ReadBack!.Quality);
        Assert.Null(result.ReadBack.Value);
        Assert.Equal(2, result.ReadBackAttempts);
        Assert.Equal(1, h.State.Writes);
    }

    [Fact]
    public async Task VerificationTimeoutReportsSubmissionWithoutFalseReadbackConfirmation()
    {
        await using var h = new OpHarness();
        await h.StartReadyAsync();
        h.State.BlockVerificationRead = true;
        var result = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "42", new WriteVerificationOptions { TimeoutMs = 50 });
        Assert.Equal(WriteCommandState.TimedOut, result.State);
        Assert.True(result.WriteAttempted);
        Assert.True(result.Submitted);
        Assert.Equal(1, result.ReadBackAttempts);
        Assert.Null(result.ReadBack);
        Assert.Equal(1, h.State.Disposals);
        Assert.Equal(0, h.State.DisposedDuringOperation);
    }

    [Fact]
    public async Task StoppingDuringVerificationCancelsOnlyTheTargetAndJoinsItsRead()
    {
        await using var h = new OpHarness(c => c.Devices.Add(PeerDevice()));
        await h.StartReadyAsync();
        var peer = h.Config.Devices[1];
        h.State.BlockVerificationRead = true;
        var command = h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "42");
        await h.State.VerificationReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await h.Runtime.StopDeviceAsync(h.Device.Id).WaitAsync(TimeSpan.FromSeconds(3));
        var result = await command;
        Assert.Equal(WriteCommandState.Cancelled, result.State);
        Assert.True(result.Submitted);
        Assert.NotEqual(WriteCommandState.Matched, result.State);
        Assert.Equal(0, h.State.DisposedDuringOperation);
        Assert.True(h.Runtime.IsDeviceRunning(peer.Id));
        var count = h.Factory.State(peer.Id).Reads;
        await Until(() => h.Factory.State(peer.Id).Reads > count);
        Assert.Equal(1, h.Factory.State(peer.Id).Creations);
        var refused = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "43");
        Assert.Equal(WriteCommandState.Failed, refused.State);
        Assert.False(refused.WriteAttempted);
    }

    [Fact]
    public async Task ConfigurationRestartCancelsOldVerificationBeforeReusingTheDeviceId()
    {
        await using var h = new OpHarness(c => c.Devices.Add(PeerDevice()));
        await h.StartReadyAsync();
        h.State.BlockVerificationRead = true;
        var command = h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "42");
        await h.State.VerificationReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var changed = JsonSerializer.Deserialize<GatewayConfiguration>(JsonSerializer.Serialize(h.Config))!;
        changed.Devices[0].Name = "changed";
        var applied = await h.Runtime.ApplyConfigurationAsync(changed, new OpConfigurationStore()).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(WriteCommandState.Cancelled, (await command).State);
        Assert.Contains(h.Device.Id, applied.Restarted);
        Assert.Equal(1, h.State.Writes);
        Assert.Equal(1, h.State.MaximumLiveDrivers);
        Assert.Equal(0, h.State.DisposedDuringOperation);
        Assert.Equal(1, h.Factory.State(h.Config.Devices[1].Id).Creations);
    }

    [Fact]
    public async Task ChannelAdmissionDelayDoesNotConsumeTheCommunicationDeadline()
    {
        await using var h = new OpHarness(c =>
        {
            var channel = new ChannelConfiguration { Name = "shared" };
            c.Channels.Add(channel);
            c.Devices[0].ChannelId = channel.Id;
            c.Devices[0].TimeoutMs = 2000;
            var peer = PeerDevice();
            peer.PollIntervalMs = 60_000;
            peer.TimeoutMs = 100;
            peer.ChannelId = channel.Id;
            c.Devices.Add(peer);
        });
        await h.StartReadyAsync();
        var release = h.State.ReadRelease = NewSignal();
        h.State.ReadEntered = NewSignal();
        var probe = h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id);
        await h.State.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var target = h.Config.Devices[1];
        // A writable peer shares the same serialized channel, but has its own short device deadline.
        var command = h.Runtime.WriteVerifiedAsync(target.Id, target.Tags[0].Id, "42", new WriteVerificationOptions { TimeoutMs = 50 });
        try
        {
            await Task.Delay(150);
            Assert.False(command.IsCompleted, "Waiting for another device's channel lease is not a communication timeout.");
            Assert.Equal(0, h.Factory.State(target.Id).Writes);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(DeviceProbeState.Succeeded, (await probe).State);
        var result = await command;
        Assert.Equal(WriteCommandState.Matched, result.State);
    }

    [Fact]
    public async Task ConcurrentCommandsCannotInterleaveBetweenWriteAndItsOwnReadback()
    {
        await using var h = new OpHarness();
        await h.StartReadyAsync();
        h.State.ReadDelayMs = 5;
        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(value => h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id,
            value.ToString(CultureInfo.InvariantCulture), new WriteVerificationOptions { MaxAttempts = 1 })));
        Assert.All(results, result => Assert.Equal(WriteCommandState.Matched, result.State));
        Assert.Equal(8, results.Select(result => result.CommandId).Distinct().Count());
        Assert.Equal(8, h.State.Writes);
        Assert.Equal(1, h.State.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task LegacyWriteRemainsSubmissionOnlyAndRetainsValidationExceptions()
    {
        await using var h = new OpHarness(c => c.Devices[0].Tags[0].DataType = TagDataType.UInt64);
        await h.StartReadyAsync();
        h.State.AfterWriteValue = _ => 0UL;
        var reads = h.State.Reads;
        await h.Runtime.WriteAsync(h.Device.Id, h.Tag.Id, "18446744073709551615");
        Assert.Equal(ulong.MaxValue, h.State.LastWrite);
        Assert.Equal(reads, h.State.Reads);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Runtime.WriteAsync(h.Device.Id, h.Tag.Id, "18446744073709551616"));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Runtime.WriteAsync(h.Device.Id, h.Tag.Id, "1.5"));
        Assert.Equal(1, h.State.Writes);
    }

    [Fact]
    public async Task InvalidVerificationOptionsAndDisabledRemotePermissionNeverReachTheDriver()
    {
        await using var h = new OpHarness();
        await h.StartReadyAsync();
        var options = new[]
        {
            new WriteVerificationOptions { TimeoutMs = 0 }, new WriteVerificationOptions { MaxAttempts = 21 },
            new WriteVerificationOptions { RetryIntervalMs = -1 }, new WriteVerificationOptions { RelativeTolerance = double.NaN },
            new WriteVerificationOptions { AbsoluteTolerance = double.PositiveInfinity }, new WriteVerificationOptions { CommandId = Guid.Empty },
            new WriteVerificationOptions { Source = "bad\nsource" }, new WriteVerificationOptions { RequireRemoteWritePermission = true }
        };
        foreach (var option in options)
        {
            var result = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "3", option);
            Assert.Equal(WriteCommandState.Failed, result.State);
            Assert.False(result.WriteAttempted);
        }
        Assert.Equal(0, h.State.Writes);
    }

    [Fact]
    public async Task TemporaryProbeRetainsLegacyQuarantineUntilOriginalNativeCallActuallyEnds()
    {
        var log = new GatewayLog();
        var factory = new IotGatewayDriverFactory(typeof(AdapterLifecycleTests.OriginalDriver),
            new IotGatewayDriverCatalog.DriverDefinition("PLC.SiemensS7", typeof(AdapterLifecycleTests.OriginalDriver).FullName!, "fake", "Read", ""), log);
        var device = new DeviceConfiguration
        {
            Name = Guid.NewGuid().ToString("N"), DriverId = "PLC.SiemensS7", TimeoutMs = 1000, PollIntervalMs = 200,
            Properties = new() { ["BlockRead"] = "true" },
            Tags = [new TagConfiguration { Name = "value", Address = "DB1.DBW0", DataType = TagDataType.UInt16, ReadMethod = "Read" }]
        };
        await using var runtime = new GatewayRuntime(new GatewayConfiguration { Devices = [device] }, new DriverRegistry([factory], log), new OpStore(), new OpPublisher(), log);
        using var cancellation = new CancellationTokenSource();
        var pending = runtime.ProbeDeviceAsync(device.Id, device.Tags[0].Id, cancellation.Token);
        await Until(() => AdapterLifecycleTests.OriginalDriver.Probes.TryGetValue(device.Name, out var observed) && observed.ReadEntered.IsSet);
        var native = AdapterLifecycleTests.OriginalDriver.Probes[device.Name];
        try
        {
            cancellation.Cancel();
            var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(DeviceProbeState.Cancelled, cancelled.State);
            Assert.DoesNotContain(native.Calls, call => call.Operation is "close" or "dispose");
            var refused = await runtime.ProbeDeviceAsync(device.Id, device.Tags[0].Id);
            Assert.Equal(DeviceProbeState.Failed, refused.State);
            Assert.Contains("上次原驱动调用尚未退出", refused.Error);
            Assert.Same(native, AdapterLifecycleTests.OriginalDriver.Probes[device.Name]);
        }
        finally { native.ReadRelease.Set(); }
        await Until(() => native.Calls.Any(call => call.Operation == "dispose"));
        Assert.Single(native.Calls.Select(call => call.ThreadId).Distinct());
        Assert.False(runtime.IsRunning);
    }

    private static DeviceConfiguration PeerDevice() => new()
    {
        Name = "peer", DriverId = "operation-test", PollIntervalMs = 100, TimeoutMs = 1000,
        Tags = [new TagConfiguration { Name = "peer-value", Address = "1", DataType = TagDataType.Float64, Writable = true }]
    };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(4);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Device operation test did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    private sealed class OpHarness : IAsyncDisposable
    {
        public GatewayConfiguration Config { get; } = new()
        {
            Devices = [new DeviceConfiguration { Name = "operation", DriverId = "operation-test", PollIntervalMs = 60_000, TimeoutMs = 1000,
                Tags = [new TagConfiguration { Name = "value", Address = "0", DataType = TagDataType.Float64, Writable = true }] }]
        };
        public DeviceConfiguration Device => Config.Devices[0];
        public TagConfiguration Tag => Device.Tags[0];
        public OpState State => Factory.State(Device.Id);
        public OpFactory Factory { get; }
        public OpStore Store { get; } = new();
        public GatewayLog Log { get; } = new();
        public GatewayRuntime Runtime { get; }
        public OpHarness(Action<GatewayConfiguration>? configure = null, string driverId = "operation-test")
        {
            configure?.Invoke(Config);
            Factory = new OpFactory(driverId);
            Runtime = new GatewayRuntime(Config, new DriverRegistry([Factory], Log), Store, new OpPublisher(), Log);
        }
        public async Task StartReadyAsync()
        {
            await Runtime.StartAsync();
            await Until(() => Runtime.GetLatest().Count == Config.Devices.Where(d => d.Enabled).Sum(d => d.Tags.Count(t => t.Enabled)));
        }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class OpState
    {
        public int Creations, Connections, Reads, Writes, Disposals, LiveDrivers, MaximumLiveDrivers;
        public int ActiveOperations, MaximumConcurrentOperations, DisposedDuringOperation, ReadsAfterWrite;
        public object Value = 2d;
        public object? LastWrite;
        public bool FailConnect, BlockVerificationRead;
        public int ReadDelayMs;
        public string? ReadbackFault;
        public Func<int, object>? AfterWriteValue;
        public TaskCompletionSource? ReadRelease;
        public TaskCompletionSource ReadEntered { get; set; } = NewSignal();
        public TaskCompletionSource VerificationReadEntered { get; } = NewSignal();
    }

    private sealed class OpFactory(string id) : IDriverFactory
    {
        private readonly ConcurrentDictionary<Guid, OpState> _states = new();
        public OpState State(Guid deviceId) => _states.GetOrAdd(deviceId, _ => new());
        public DriverDescriptor Descriptor { get; } = new(id, "Operation test", "No external IO", true);
        public IReadOnlyList<string> Validate(DeviceConfiguration configuration) => [];
        public IDeviceDriver Create(DeviceConfiguration configuration)
        {
            var state = State(configuration.Id);
            Interlocked.Increment(ref state.Creations);
            var live = Interlocked.Increment(ref state.LiveDrivers);
            state.MaximumLiveDrivers = Math.Max(state.MaximumLiveDrivers, live);
            return new OpDriver(state);
        }
    }

    private sealed class OpDriver(OpState state) : IDeviceDriver
    {
        private bool _connected;
        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref state.Connections);
            if (state.FailConnect) throw new IOException("connect failed");
            _connected = true;
            return Task.CompletedTask;
        }
        public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                Interlocked.Increment(ref state.Reads);
                state.ReadEntered.TrySetResult();
                if (state.ReadRelease is { } release) await release.Task.WaitAsync(cancellationToken);
                var attempt = 0;
                if (Volatile.Read(ref state.Writes) > 0)
                {
                    attempt = Interlocked.Increment(ref state.ReadsAfterWrite);
                    state.VerificationReadEntered.TrySetResult();
                    if (state.BlockVerificationRead) await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                if (state.ReadDelayMs > 0) await Task.Delay(state.ReadDelayMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var values = tags.Select(tag => new TagReadResult(tag.Id, attempt > 0 && state.AfterWriteValue is { } read ? read(attempt) : state.Value,
                    attempt > 0 && state.ReadbackFault == "bad" ? SampleQuality.Bad : SampleQuality.Good, state.ReadbackFault)).ToArray();
                return attempt > 0 ? state.ReadbackFault switch
                {
                    "missing" => [], "duplicate" => values.Concat(values).ToArray(), _ => values
                } : values;
            }
            finally { Interlocked.Decrement(ref state.ActiveOperations); }
        }
        public Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                state.LastWrite = value;
                state.Value = value;
                Interlocked.Increment(ref state.Writes);
                return Task.CompletedTask;
            }
            finally { Interlocked.Decrement(ref state.ActiveOperations); }
        }
        private void Enter()
        {
            if (!_connected) throw new InvalidOperationException("driver is not connected");
            var active = Interlocked.Increment(ref state.ActiveOperations);
            state.MaximumConcurrentOperations = Math.Max(state.MaximumConcurrentOperations, active);
        }
        public ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref state.ActiveOperations) > 0) Interlocked.Increment(ref state.DisposedDuringOperation);
            _connected = false;
            Interlocked.Increment(ref state.Disposals);
            Interlocked.Decrement(ref state.LiveDrivers);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OpStore : IGatewayStore
    {
        public int Initializations;
        public Task InitializeAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref Initializations); return Task.CompletedTask; }
        public Task<bool> EnqueueAsync(string topic, string payload, int maxMessages, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OutboxMessage>>([]);
        public Task AcknowledgeAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new StoreStatistics(0));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class OpPublisher : ITelemetryPublisher
    {
        public bool IsConnected => false;
        public Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class OpConfigurationStore : IConfigurationStore
    {
        public Task<GatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(GatewayConfiguration configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
