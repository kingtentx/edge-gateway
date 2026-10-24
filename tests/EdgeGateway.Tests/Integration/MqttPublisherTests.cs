using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.Integration;

public sealed class MqttPublisherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectUsesLiteralCredentialsAndReconnectsWhenTheyChange(bool tokenAuthentication)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = Settings(listener);
        settings.Username = "test-account";
        settings.Password = "  test-password;quotes\"!  ";
        settings.AccessToken = tokenAuthentication ? "  test-token/with+symbols=  " : "";
        var expected = new[]
        {
            (User: tokenAuthentication ? settings.AccessToken : settings.Username, Password: settings.Password),
            (User: tokenAuthentication ? "rotated-test-token!" : settings.Username, Password: "rotated-test-password!")
        };
        var server = Task.Run(async () =>
        {
            foreach (var credential in expected)
            {
                using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                var stream = peer.GetStream();
                var connect = await ReadFrameAsync(stream, deadline.Token);
                Assert.Equal(0x10, connect.Header);
                Assert.Equal(0xC0, connect.Payload[7] & 0xC0); // User name and password flags.
                var position = 10;
                string ReadString()
                {
                    var length = BinaryPrimitives.ReadUInt16BigEndian(connect.Payload.AsSpan(position, 2));
                    position += 2;
                    var value = Encoding.UTF8.GetString(connect.Payload, position, length);
                    position += length;
                    return value;
                }
                Assert.Equal(settings.ClientId, ReadString());
                Assert.Equal(credential.User, ReadString());
                Assert.Equal(credential.Password, ReadString());
                Assert.Equal(connect.Payload.Length, position);
                await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
                Assert.Equal(0xE0, (await ReadFrameAsync(stream, deadline.Token)).Header); // DISCONNECT.
            }
        }, deadline.Token);
        await using var publisher = new MqttTelemetryPublisher();
        await publisher.EnsureConnectedAsync(settings, [], deadline.Token);
        settings.Password = expected[1].Password;
        if (tokenAuthentication) settings.AccessToken = expected[1].User;
        await publisher.EnsureConnectedAsync(settings, [], deadline.Token);
        await publisher.DisconnectAsync(deadline.Token);
        await server.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task QosOnePublishCompletesOnlyAfterMatchingBrokerAcknowledgement()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = new TaskCompletionSource<(string Topic, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            var connect = await ReadFrameAsync(stream, deadline.Token);
            Assert.Equal(0x10, connect.Header);
            // MQTT 3.1.1 protocol name and version are an independent wire-level assertion.
            Assert.Equal(new byte[] { 0, 4, 77, 81, 84, 84, 4 }, connect.Payload[..7]);
            await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
            var publish = await ReadFrameAsync(stream, deadline.Token);
            Assert.Equal(0x32, publish.Header); // PUBLISH, QoS1, no retain, first transmission.
            var topicLength = BinaryPrimitives.ReadUInt16BigEndian(publish.Payload);
            var packetId = publish.Payload.AsSpan(2 + topicLength, 2).ToArray();
            received.TrySetResult((Encoding.UTF8.GetString(publish.Payload, 2, topicLength),
                Encoding.UTF8.GetString(publish.Payload, 4 + topicLength, publish.Payload.Length - 4 - topicLength)));
            await acknowledge.Task.WaitAsync(deadline.Token);
            await stream.WriteAsync(new byte[] { 0x40, 2, packetId[0], packetId[1] }, deadline.Token);
            await close.Task.WaitAsync(deadline.Token);
        }, deadline.Token);

        await using var publisher = new MqttTelemetryPublisher();
        var settings = Settings(listener);
        const string payload = "{\"messageId\":\"test-001\",\"value\":42.5,\"name\":\"温度\"}";
        var publishing = publisher.PublishAsync(settings, "edge/test/telemetry", payload, deadline.Token);
        try
        {
            var packet = await received.Task.WaitAsync(deadline.Token);
            Assert.Equal("edge/test/telemetry", packet.Topic);
            Assert.Equal(payload, packet.Payload);
            Assert.False(publishing.IsCompleted, "未收到 PUBACK 不得报告发布完成。");
            acknowledge.TrySetResult();
            await publishing.WaitAsync(deadline.Token);
            Assert.True(publisher.IsConnected);
            await publisher.DisconnectAsync(deadline.Token);
            Assert.False(publisher.IsConnected);
        }
        finally
        {
            acknowledge.TrySetResult();
            close.TrySetResult();
            await server.WaitAsync(deadline.Token);
        }
    }

    [Fact]
    public async Task MissingPubAckFailsAndDisconnectsInsteadOfReportingSuccess()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            await ReadFrameAsync(stream, deadline.Token);
            await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
            var frame = await ReadFrameAsync(stream, deadline.Token);
            Assert.Equal(0x32, frame.Header);
            received.TrySetResult();
            await close.Task.WaitAsync(deadline.Token);
        }, deadline.Token);
        await using var publisher = new MqttTelemetryPublisher();
        var settings = Settings(listener);
        settings.TimeoutMs = 800;
        try
        {
            var publishing = publisher.PublishAsync(settings, "edge/test", "{}", deadline.Token);
            await received.Task.WaitAsync(deadline.Token);
            await Assert.ThrowsAnyAsync<Exception>(() => publishing);
            Assert.False(publisher.IsConnected);
        }
        finally
        {
            close.TrySetResult();
            await server.WaitAsync(deadline.Token);
        }
    }

    [Fact]
    public async Task TlsRejectsAnUntrustedServerCertificate()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var alternative = new SubjectAlternativeNameBuilder();
        alternative.AddDnsName("localhost");
        alternative.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(alternative.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var ssl = new SslStream(peer.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
                var buffer = new byte[1];
                await ssl.ReadAsync(buffer, deadline.Token);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        }, deadline.Token);
        await using var publisher = new MqttTelemetryPublisher();
        var settings = Settings(listener);
        settings.UseTls = true;
        var failure = await Record.ExceptionAsync(() => publisher.PublishAsync(settings, "edge/test", "{}", deadline.Token));
        Assert.NotNull(failure);
        Assert.False(publisher.IsConnected);
        await server.WaitAsync(deadline.Token);
    }

    private static MqttConfiguration Settings(TcpListener listener) => new()
    {
        Enabled = true, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        ClientId = "edge-test-" + Guid.NewGuid().ToString("N"), TimeoutMs = 5000
    };

    private static async Task<(byte Header, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellationToken);
        var header = one[0];
        var length = 0;
        var multiplier = 1;
        for (var i = 0; i < 4; i++)
        {
            await stream.ReadExactlyAsync(one, cancellationToken);
            length += (one[0] & 0x7F) * multiplier;
            if ((one[0] & 0x80) == 0)
            {
                Assert.InRange(length, 0, 65536);
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, cancellationToken);
                return (header, payload);
            }
            multiplier *= 128;
        }
        throw new InvalidDataException("MQTT Remaining Length is invalid.");
    }
}
