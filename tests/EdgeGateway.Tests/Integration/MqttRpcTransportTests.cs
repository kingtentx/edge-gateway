using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EdgeGateway.Model;
using EdgeGateway.DataAccess;
using EdgeGateway.Driver.Simulator;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.Integration;

public sealed class MqttRpcTransportTests
{
    [Fact]
    public async Task SubscribesReceivesCommandRejectsRetainedAndPublishesReplyWithPubAck()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<(string Topic, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string request = "{\"id\":\"r-1\",\"device\":\"pump\",\"method\":\"read\",\"params\":{\"tag\":\"speed\"}}";
        var broker = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            Assert.Equal(0x10, (await ReadFrame(stream, deadline.Token)).Header);
            await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
            var subscribe = await ReadFrame(stream, deadline.Token);
            Assert.Equal(0x82, subscribe.Header);
            var topicLength = BinaryPrimitives.ReadUInt16BigEndian(subscribe.Payload.AsSpan(2));
            Assert.Equal("edge/test/rpc/request", Encoding.UTF8.GetString(subscribe.Payload, 4, topicLength));
            Assert.Equal(1, subscribe.Payload[^1]);
            await stream.WriteAsync(new byte[] { 0x90, 3, subscribe.Payload[0], subscribe.Payload[1], 1 }, deadline.Token);
            await stream.WriteAsync(Publish(0x33, "edge/test/rpc/request", request, 11), deadline.Token);
            await stream.WriteAsync(Publish(0x32, "edge/test/rpc/request", request, 12), deadline.Token);
            for (var i = 0; i < 4; i++)
            {
                var frame = await ReadFrame(stream, deadline.Token);
                if ((frame.Header & 0xF0) == 0x40) continue;
                Assert.Equal(0x32, frame.Header);
                var count = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload);
                reply.TrySetResult((Encoding.UTF8.GetString(frame.Payload, 2, count), Encoding.UTF8.GetString(frame.Payload, count + 4, frame.Payload.Length - count - 4)));
                await stream.WriteAsync(new byte[] { 0x40, 2, frame.Payload[count + 2], frame.Payload[count + 3] }, deadline.Token);
                break;
            }
            await done.Task.WaitAsync(deadline.Token);
        }, deadline.Token);
        await using var publisher = new MqttTelemetryPublisher();
        var settings = new MqttConfiguration { Enabled = true, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, ClientId = "rpc-wire", TimeoutMs = 3000 };
        try
        {
            await publisher.EnsureConnectedAsync(settings, ["edge/test/rpc/request"], deadline.Token);
            var command = await publisher.ReceiveAsync(deadline.Token);
            Assert.Equal(request, command.Payload);
            Assert.False(command.Retained);
            Assert.Equal(1, publisher.DroppedMessages);
            const string response = "{\"id\":\"r-1\",\"data\":{\"success\":true}}";
            await publisher.PublishAsync(settings, "edge/test/rpc/response", response, deadline.Token);
            Assert.Equal(("edge/test/rpc/response", response), await reply.Task.WaitAsync(deadline.Token));
            Assert.Equal(1, publisher.ConnectionVersion);
        }
        finally { done.TrySetResult(); await broker.WaitAsync(deadline.Token); }
    }

    [Fact]
    public async Task SubscriptionRejectionDisconnectsAndDoesNotReportReady()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var broker = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            await ReadFrame(stream, deadline.Token);
            await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
            var subscribe = await ReadFrame(stream, deadline.Token);
            await stream.WriteAsync(new byte[] { 0x90, 3, subscribe.Payload[0], subscribe.Payload[1], 0x80 }, deadline.Token);
            await ReadFrame(stream, deadline.Token);
        }, deadline.Token);
        await using var publisher = new MqttTelemetryPublisher();
        var settings = new MqttConfiguration { Enabled = true, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, TimeoutMs = 3000 };
        await Assert.ThrowsAsync<IOException>(() => publisher.EnsureConnectedAsync(settings, ["edge/test/rpc/request"], deadline.Token));
        Assert.False(publisher.IsConnected);
        Assert.Equal(0, publisher.ConnectionVersion);
        await broker.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task EndToEndRpcOverTcpRejectsWritePermissionAndLeavesTheDeviceUnchanged()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            await ReadFrame(stream, deadline.Token);
            await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
            var subscribe = await ReadFrame(stream, deadline.Token);
            Assert.Equal(0x82, subscribe.Header);
            await stream.WriteAsync(new byte[] { 0x90, 3, subscribe.Payload[0], subscribe.Payload[1], 1 }, deadline.Token);
            await send.Task.WaitAsync(deadline.Token);
            await stream.WriteAsync(Publish(0x32, "edge/edge-001/rpc/request",
                "{\"id\":\"wire-denied\",\"device\":\"pump\",\"method\":\"write\",\"params\":{\"tag\":\"speed\",\"value\":25}}", 15), deadline.Token);
            while (true)
            {
                var frame = await ReadFrame(stream, deadline.Token);
                if ((frame.Header & 0xF0) == 0x40) continue;
                Assert.Equal(0x32, frame.Header);
                var size = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload);
                Assert.Equal("edge/edge-001/rpc/response", Encoding.UTF8.GetString(frame.Payload, 2, size));
                received.TrySetResult(Encoding.UTF8.GetString(frame.Payload, size + 4, frame.Payload.Length - size - 4));
                await stream.WriteAsync(new byte[] { 0x40, 2, frame.Payload[size + 2], frame.Payload[size + 3] }, deadline.Token);
                break;
            }
            await close.Task.WaitAsync(deadline.Token);
        }, deadline.Token);
        var directory = Path.Combine(Path.GetTempPath(), "edge-rpc-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileGatewayStore(Path.Combine(directory, "messaging"));
            await using var publisher = new MqttTelemetryPublisher();
            var config = new GatewayConfiguration
            {
                PublishIntervalMs = 3600000,
                Mqtt = new() { Enabled = true, RpcEnabled = true, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, TimeoutMs = 3000 },
                Devices = [new() { Name = "pump", DriverId = "simulator", PollIntervalMs = 3600000,
                    Tags = [new() { Name = "speed", Address = "constant:2", DataType = TagDataType.UInt16, Writable = true }] }]
            };
            var log = new GatewayLog();
            await using var runtime = new GatewayRuntime(config, new DriverRegistry([new SimulatorDriverFactory()], log), store, publisher, log);
            await runtime.StartAsync(deadline.Token);
            while (!runtime.GetLatest().Any(sample => sample.Quality == SampleQuality.Good)) await Task.Delay(10, deadline.Token);
            send.TrySetResult();
            using var response = JsonDocument.Parse(await received.Task.WaitAsync(deadline.Token));
            Assert.Equal("WriteDenied", response.RootElement.GetProperty("data").GetProperty("status").GetString());
            var probe = await runtime.ProbeDeviceAsync(config.Devices[0].Id, config.Devices[0].Tags[0].Id, deadline.Token);
            Assert.Equal(2, Convert.ToInt32(probe.Sample!.Value));
            await runtime.StopAsync();
        }
        finally
        {
            send.TrySetResult(); close.TrySetResult(); await broker.WaitAsync(deadline.Token);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ThingsCloudOrdersRegistrationAndRequiresApplicationAcknowledgements(bool historical, bool registrationRejected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmApplication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string payload = "{\"pump\":{\"speed\":2,\"_edge\":{\"messageId\":\"persisted-1\"}}}";
        var trace = new List<string>();
        var broker = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            await ReadFrame(stream, deadline.Token);
            await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
            var subscribe = await ReadFrame(stream, deadline.Token);
            Assert.Equal(0x82, subscribe.Header);
            var subscriptionText = Encoding.UTF8.GetString(subscribe.Payload);
            Assert.Contains("gateway/connect/response", subscriptionText);
            Assert.Contains("gateway/attributes/response", subscriptionText);
            await stream.WriteAsync(new byte[] { 0x90, 4, subscribe.Payload[0], subscribe.Payload[1], 1, 1 }, deadline.Token);
            async Task<string> Expect(string expected)
            {
                var frame = await ReadFrame(stream, deadline.Token);
                Assert.Equal(0x32, frame.Header);
                var count = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload);
                var topic = Encoding.UTF8.GetString(frame.Payload, 2, count);
                Assert.Equal(expected, topic); trace.Add(topic);
                await stream.WriteAsync(new byte[] { 0x40, 2, frame.Payload[count + 2], frame.Payload[count + 3] }, deadline.Token);
                return Encoding.UTF8.GetString(frame.Payload, count + 4, frame.Payload.Length - count - 4);
            }
            if (historical)
            {
                Assert.Equal(payload, await Expect("gateway/attributes"));
                await stream.WriteAsync(PublishZero("gateway/attributes/response", "{\"result\":0,\"errcode\":421,\"message\":\"not bound\"}"), deadline.Token);
            }
            var registration = await Expect("gateway/connect");
            Assert.Contains("pump", registration);
            if (historical) Assert.Contains("fixture-type", registration);
            if (registrationRejected)
            {
                await stream.WriteAsync(PublishZero("gateway/connect/response", "{\"result\":0,\"errcode\":421,\"message\":\"not bound\"}"), deadline.Token);
                reached.TrySetResult();
            }
            else
            {
                await stream.WriteAsync(PublishZero("gateway/connect/response", "{\"result\":1,\"device\":{\"sub_device_addr\":\"pump\"}}"), deadline.Token);
                Assert.Equal(payload, await Expect("gateway/attributes"));
                reached.TrySetResult();
                await confirmApplication.Task.WaitAsync(deadline.Token);
                await stream.WriteAsync(PublishZero("gateway/attributes/response", "{\"result\":1,\"ts\":1792713600000}"), deadline.Token);
                if (historical) Assert.Contains("pump", await Expect("gateway/disconnect"));
            }
            await close.Task.WaitAsync(deadline.Token);
        }, deadline.Token);
        await using var publisher = new MqttTelemetryPublisher();
        var settings = new MqttConfiguration
        { Enabled = true, Platform = MqttPlatform.ThingsCloud, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, TimeoutMs = 10000, ThingsCloudTypeKey = historical ? "fixture-type" : "" };
        try
        {
            var publish = publisher.PublishPlatformTelemetryAsync(settings, "gateway/attributes", payload, historical ? [] : ["pump"], deadline.Token);
            await reached.Task.WaitAsync(deadline.Token);
            if (registrationRejected)
            {
                var error = await Assert.ThrowsAnyAsync<IOException>(() => publish);
                Assert.Contains("421", error.Message);
                Assert.False(publisher.IsConnected);
                Assert.Equal(new[] { "gateway/connect" }, trace);
            }
            else
            {
                Assert.False(publish.IsCompleted, "Broker PUBACK 不能替代 ThingsCloud result=1。");
                confirmApplication.TrySetResult();
                await publish.WaitAsync(deadline.Token);
                Assert.Equal(historical ? new[] { "gateway/attributes", "gateway/connect", "gateway/attributes", "gateway/disconnect" }
                    : new[] { "gateway/connect", "gateway/attributes" }, trace);
            }
        }
        finally { confirmApplication.TrySetResult(); close.TrySetResult(); await broker.WaitAsync(deadline.Token); }
    }

    private static byte[] PublishZero(string topic, string payload)
    {
        var name = Encoding.UTF8.GetBytes(topic); var body = Encoding.UTF8.GetBytes(payload);
        using var stream = new MemoryStream(); stream.WriteByte(0x30);
        var length = name.Length + body.Length + 2;
        do { var digit = (byte)(length % 128); length /= 128; stream.WriteByte((byte)(digit | (length > 0 ? 128 : 0))); } while (length > 0);
        stream.WriteByte((byte)(name.Length >> 8)); stream.WriteByte((byte)name.Length); stream.Write(name); stream.Write(body);
        return stream.ToArray();
    }

    private static byte[] Publish(byte header, string topic, string payload, ushort id)
    {
        var name = Encoding.UTF8.GetBytes(topic);
        var body = Encoding.UTF8.GetBytes(payload);
        using var stream = new MemoryStream();
        stream.WriteByte(header);
        var length = name.Length + body.Length + 4;
        do { var digit = (byte)(length % 128); length /= 128; stream.WriteByte((byte)(digit | (length > 0 ? 128 : 0))); } while (length > 0);
        stream.WriteByte((byte)(name.Length >> 8)); stream.WriteByte((byte)name.Length); stream.Write(name);
        stream.WriteByte((byte)(id >> 8)); stream.WriteByte((byte)id); stream.Write(body);
        return stream.ToArray();
    }

    private static async Task<(byte Header, byte[] Payload)> ReadFrame(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellationToken);
        var header = one[0]; var length = 0; var multiplier = 1;
        for (var index = 0; index < 4; index++)
        {
            await stream.ReadExactlyAsync(one, cancellationToken);
            length += (one[0] & 127) * multiplier;
            if ((one[0] & 128) == 0)
            {
                Assert.InRange(length, 0, 65536);
                var body = new byte[length]; await stream.ReadExactlyAsync(body, cancellationToken); return (header, body);
            }
            multiplier *= 128;
        }
        throw new InvalidDataException("Invalid MQTT length.");
    }
}
