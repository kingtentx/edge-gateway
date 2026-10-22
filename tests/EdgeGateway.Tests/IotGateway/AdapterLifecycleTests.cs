using System.Collections.Concurrent;
using EdgeGateway.Abstractions;
using EdgeGateway.IotGateway;
using EdgeGateway.Model;
using EdgeGateway.Service;
using Microsoft.Extensions.Logging;
using PluginInterface;

namespace EdgeGateway.Tests.IotGateway;

public sealed class AdapterLifecycleTests
{
    [Fact]
    public async Task ConstructionReadAsyncWriteAndCleanupStayOnOneDedicatedApartment()
    {
        var factory = Factory();
        var configuration = Configuration();
        var tag = configuration.Tags[0];
        tag.Writable = true;
        var driver = factory.Create(configuration);
        await driver.ConnectAsync(CancellationToken.None);
        var probe = OriginalDriver.Probes[configuration.Name];
        try
        {
            Assert.Equal(configuration.Id.ToString("D"), probe.Driver!.DeviceId);
            Assert.Equal(configuration.TimeoutMs, probe.Driver.Timeout);
            Assert.Equal((uint)configuration.PollIntervalMs, probe.Driver.MinPeriod);
            var read = Assert.Single(await driver.ReadAsync([tag], CancellationToken.None));
            Assert.Equal(SampleQuality.Good, read.Quality);
            Assert.Equal((ushort)42, read.Value);
            await driver.WriteAsync(tag, (ushort)99, CancellationToken.None);
            Assert.Equal((ushort)99, probe.WrittenValue);
        }
        finally { await driver.DisposeAsync(); }
        Assert.Single(probe.Calls.Select(c => c.ThreadId).Distinct());
        Assert.Contains(probe.Calls, c => c.Operation == "write-before-await");
        Assert.Contains(probe.Calls, c => c.Operation == "write-after-await");
        Assert.Equal(new[] { "close", "dispose" }, probe.Calls.TakeLast(2).Select(c => c.Operation));
        if (OperatingSystem.IsWindows()) Assert.All(probe.Calls, c => Assert.Equal(ApartmentState.STA, c.Apartment));
    }

    [Theory]
    [InlineData("NullValue", "空值")]
    [InlineData("ThrowRead", "original read failure")]
    public async Task EmptyOrFailedOriginalResultsCannotBecomeGood(string parameter, string expectedError)
    {
        var configuration = Configuration();
        configuration.Properties[parameter] = "true";
        await using var driver = Factory().Create(configuration);
        await driver.ConnectAsync(CancellationToken.None);
        var result = Assert.Single(await driver.ReadAsync(configuration.Tags, CancellationToken.None));
        Assert.Equal(SampleQuality.Bad, result.Quality);
        Assert.Null(result.Value);
        Assert.Contains(expectedError, result.Error);
    }

    [Fact]
    public async Task FailedConnectionIsExplicitAndStillClosesAndDisposes()
    {
        var configuration = Configuration();
        configuration.Properties["FailConnect"] = "true";
        var driver = Factory().Create(configuration);
        await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(CancellationToken.None));
        await driver.DisposeAsync();
        var calls = OriginalDriver.Probes[configuration.Name].Calls;
        Assert.Equal(new[] { "close", "dispose" }, calls.TakeLast(2).Select(c => c.Operation));
    }

    [Fact]
    public async Task CancelledBlockingCallQuarantinesDeviceUntilOriginalThreadFinishes()
    {
        var factory = Factory();
        var configuration = Configuration();
        configuration.Properties["BlockRead"] = "true";
        var driver = factory.Create(configuration);
        await driver.ConnectAsync(CancellationToken.None);
        var probe = OriginalDriver.Probes[configuration.Name];
        using var cancellation = new CancellationTokenSource();
        var pending = driver.ReadAsync(configuration.Tags, cancellation.Token);
        try
        {
            Assert.True(probe.ReadEntered.Wait(TimeSpan.FromSeconds(2)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await driver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.DoesNotContain(probe.Calls, c => c.Operation is "close" or "dispose");
            Assert.Throws<InvalidOperationException>(() => factory.Create(configuration));
        }
        finally
        {
            probe.ReadRelease.Set();
            await driver.DisposeAsync();
        }
        IDeviceDriver? replacement = null;
        await Until(() =>
        {
            try { replacement = factory.Create(configuration); return true; }
            catch (InvalidOperationException) { return false; }
        });
        await replacement!.DisposeAsync();
        Assert.Single(probe.Calls.Select(c => c.ThreadId).Distinct());
        Assert.Equal(new[] { "close", "dispose" }, probe.Calls.TakeLast(2).Select(c => c.Operation));
    }

    [Fact]
    public async Task ExecutorTimeoutAlsoQuarantinesAndLatePostsCannotUseDisposedHandles()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext? context = null;
        var cleanupThread = 0;
        var operationThread = 0;
        var executor = new DedicatedDriverExecutor("test-timeout", () => cleanupThread = Environment.CurrentManagedThreadId, new GatewayLog());
        var pending = executor.InvokeAsync(() =>
        {
            context = SynchronizationContext.Current;
            operationThread = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            release.Wait();
            return 1;
        }, 100, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<TimeoutException>(() => pending);
            Assert.True(executor.IsQuarantined);
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.InvokeAsync(() => 2, 100, CancellationToken.None));
        }
        finally { release.Set(); await executor.StopAsync(2000); }
        Assert.True(executor.IsFinished);
        Assert.Equal(operationThread, cleanupThread);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => executor.StopAsync().AsTask()));
        var callbackRan = false;
        context!.Post(_ => callbackRan = true, null);
        Assert.False(callbackRan);
    }

    [Fact]
    public void ReflectionParametersAreStrictlyTypedAndUnknownKeysAreRejected()
    {
        var factory = Factory();
        var config = Configuration();
        config.Properties["FailConnect"] = "not-a-boolean";
        config.Properties["UnrecognizedSetting"] = "1";
        var errors = factory.Validate(config);
        Assert.Contains(errors, e => e.Contains("FailConnect"));
        Assert.Contains(errors, e => e.Contains("UnrecognizedSetting"));
        Assert.Throws<ArgumentException>(() => factory.Create(config));
    }

    private static IotGatewayDriverFactory Factory() => new(typeof(OriginalDriver),
        new IotGatewayDriverCatalog.DriverDefinition("PLC.SiemensS7", typeof(OriginalDriver).FullName!, "fake", "Read", ""), new GatewayLog());

    private static DeviceConfiguration Configuration() => new()
    {
        Name = Guid.NewGuid().ToString("N"), DriverId = "PLC.SiemensS7", TimeoutMs = 1000, PollIntervalMs = 200,
        Tags = [new TagConfiguration { Name = "value", Address = "DB1.DBW0", DataType = TagDataType.UInt16, ReadMethod = "Read" }]
    };

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("驱动隔离清理未完成。");
            await Task.Delay(10);
        }
    }

    public sealed class Probe
    {
        public OriginalDriver? Driver;
        public ConcurrentQueue<(string Operation, int ThreadId, ApartmentState Apartment)> Calls { get; } = new();
        public ManualResetEventSlim ReadEntered { get; } = new();
        public ManualResetEventSlim ReadRelease { get; } = new();
        public object? WrittenValue;
        public void Record(string operation) => Calls.Enqueue((operation, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
    }

    public sealed class OriginalDriver : IDriver
    {
        public static ConcurrentDictionary<string, Probe> Probes { get; } = new();
        private readonly Probe _probe;
        public OriginalDriver(string name, ILogger logger)
        {
            _logger = logger;
            _probe = new Probe { Driver = this };
            if (name != "metadata") Probes[name] = _probe;
            _probe.Record("construct");
        }
        [ConfigParameter("ID")] public string DeviceId { get; set; } = "";
        [ConfigParameter("超时")] public int Timeout { get; set; } = 1000;
        [ConfigParameter("周期")] public uint MinPeriod { get; set; } = 1000;
        [ConfigParameter("连接失败")] public bool FailConnect { get; set; }
        [ConfigParameter("返回空值")] public bool NullValue { get; set; }
        [ConfigParameter("读取失败")] public bool ThrowRead { get; set; }
        [ConfigParameter("阻塞读取")] public bool BlockRead { get; set; }
        public bool IsConnected { get; private set; }
        public ILogger _logger { get; set; }
        public bool Connect() { _probe.Record("connect"); return IsConnected = !FailConnect; }
        public bool Close() { _probe.Record("close"); IsConnected = false; return true; }
        public void Dispose() { _probe.Record("dispose"); }
        [Method("协议读取")]
        public DriverReturnValueModel Read(DriverAddressIoArgModel ioArg)
        {
            _probe.Record("read");
            if (BlockRead) { _probe.ReadEntered.Set(); _probe.ReadRelease.Wait(); }
            if (ThrowRead) throw new IOException("original read failure");
            return new DriverReturnValueModel { StatusType = VaribaleStatusTypeEnum.Good, Value = NullValue ? null! : (ushort)42 };
        }
        public async Task<RpcResponse> WriteAsync(string requestId, string method, DriverAddressIoArgModel argument)
        {
            _probe.Record("write-before-await");
            await Task.Delay(5);
            _probe.Record("write-after-await");
            _probe.WrittenValue = argument.Value;
            return new RpcResponse { IsSuccess = true };
        }
    }
}
