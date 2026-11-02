using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EdgeGateway.DataAccess;
using EdgeGateway.Driver.Simulator;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.Integration;

public sealed partial class MqttRpcTransportTests
{
    [Fact]
    public async Task HotReconnectOverTcpRetainsUnacknowledgedMessageAndDeviceSession()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var firstPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = new ConcurrentQueue<string>();
        var bodies = new ConcurrentQueue<string>();
        var broker = Task.Run(async () =>
        {
            try
            {
                for (var session = 0; session < 2; session++)
                {
                    using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                    var stream = peer.GetStream();
                    var connect = await ReadFrame(stream, deadline.Token);
                    Assert.Equal(0x10, connect.Header);
                    connections.Enqueue(Encoding.UTF8.GetString(connect.Payload));
                    await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
                    while (true)
                    {
                        var frame = await ReadFrame(stream, deadline.Token);
                        if (frame.Header == 0xe0) break;
                        if (frame.Header == 0xc0)
                        { await stream.WriteAsync(new byte[] { 0xd0, 0 }, deadline.Token); continue; }
                        Assert.Equal(0x32, frame.Header);
                        var length = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload);
                        bodies.Enqueue(Encoding.UTF8.GetString(frame.Payload, length + 4, frame.Payload.Length - length - 4));
                        if (session == 0)
                        {
                            // No PUBACK: applying settings must cancel this send, retain
                            // the row and wait for disconnect before opening the next session.
                            firstPublish.TrySetResult();
                        }
                        else
                        {
                            await stream.WriteAsync(new byte[] { 0x40, 2, frame.Payload[length + 2], frame.Payload[length + 3] }, deadline.Token);
                            secondPublish.TrySetResult();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }, deadline.Token);
        var directory = Path.Combine(Path.GetTempPath(), "edge-hot-mqtt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileGatewayStore(Path.Combine(directory, "messaging"));
            await using var configurationStore = new SqliteConfigurationStore(Path.Combine(directory, "config.db"));
            await using var publisher = new MqttTelemetryPublisher();
            var device = new DeviceConfiguration { DriverId = "simulator", PollIntervalMs = 100,
                Tags = [new() { Name = "value", Address = "ramp", DataType = TagDataType.Float64 }] };
            var config = new GatewayConfiguration { Devices = [device], PublishIntervalMs = 60000,
                Mqtt = new() { Enabled = true, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                    ClientId = "before-hot", TimeoutMs = 10000 } };
            await configurationStore.SaveAsync(config, deadline.Token);
            await store.InitializeAsync(deadline.Token);
            await store.EnqueueAsync("original/topic", "original-payload", 100, deadline.Token);
            var log = new GatewayLog();
            await using var runtime = new GatewayRuntime(config, new DriverRegistry([new SimulatorDriverFactory()], log), store, publisher, log);
            await runtime.StartAsync(deadline.Token);
            await firstPublish.Task.WaitAsync(deadline.Token);
            while (runtime.GetLatest().Count == 0) await Task.Delay(20, deadline.Token);
            var sample = Assert.Single(runtime.GetLatest());
            config.Mqtt.ClientId = "after-hot";
            var result = await runtime.ApplyConfigurationAsync(config, configurationStore, deadline.Token);
            Assert.Empty(result.Restarted); Assert.Empty(result.Stopped);
            await secondPublish.Task.WaitAsync(deadline.Token);
            while ((await store.GetStatisticsAsync(deadline.Token)).OutboxCount != 0) await Task.Delay(20, deadline.Token);
            Assert.Equal(new[] { "original-payload", "original-payload" }, bodies.ToArray());
            Assert.Contains("before-hot", connections.First());
            Assert.Contains("after-hot", connections.Last());
            while (runtime.GetLatestSample(sample.TagId)!.Timestamp <= sample.Timestamp) await Task.Delay(20, deadline.Token);
            await runtime.StopPlatformAsync(deadline.Token);
            Assert.True(runtime.IsDeviceRunning(device.Id));
            Assert.False(publisher.IsConnected);
            await broker.WaitAsync(deadline.Token);
        }
        finally
        {
            deadline.Cancel();
            await broker;
            Directory.Delete(directory, true);
        }
    }
}
