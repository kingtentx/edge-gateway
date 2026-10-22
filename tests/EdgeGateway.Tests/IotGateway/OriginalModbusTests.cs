using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.IotGateway;

public sealed class OriginalModbusTests(CatalogFixture catalog) : IClassFixture<CatalogFixture>
{
    [Theory]
    [InlineData("HoldingRegisters")]
    [InlineData("Read")]
    public async Task RealOriginalModbusMethodsReadNetworkRegistersInsteadOfSampleData(string method)
    {
        await using var server = new Server(async (stream, token) =>
        {
            var request = await ReadRequest(stream, token);
            Assert.Equal(Convert.FromHexString("0300070001"), request.AsSpan(7).ToArray());
            await stream.WriteAsync(Response(request, [3, 2, 0x12, 0x34]), token);
        });
        var tag = new TagConfiguration { Address = "7", ReadMethod = method, DataType = TagDataType.UInt16, Endian = ProtocolEndian.BigEndian };
        await using var driver = catalog.Get("PLC.ModBusMaster").Create(server.Configuration([tag]));
        await driver.ConnectAsync(server.Token);
        var sample = Assert.Single(await driver.ReadAsync([tag], server.Token));
        Assert.Equal(SampleQuality.Good, sample.Quality);
        Assert.Equal((ushort)0x1234, Assert.IsType<ushort>(sample.Value));
        await server.Completion;
    }

    [Fact]
    public async Task RealOriginalBatchReadPopulatesBooleanAndBitCacheInConfiguredOrder()
    {
        await using var server = new Server(async (stream, token) =>
        {
            var request = await ReadRequest(stream, token);
            Assert.Equal(Convert.FromHexString("01000A0003"), request.AsSpan(7).ToArray());
            await stream.WriteAsync(Response(request, [1, 1, 0b00000101]), token);
        });
        var tags = new List<TagConfiguration>
        {
            new() { Name = "batch", Address = "1,10,3,coils", ReadMethod = "ReadMultiple", DataType = TagDataType.Any },
            new() { Name = "bool", Address = "coils,10", ReadMethod = "ReadFromCache", DataType = TagDataType.Boolean },
            new() { Name = "bit", Address = "coils,11", ReadMethod = "ReadFromCache", DataType = TagDataType.Bit }
        };
        await using var driver = catalog.Get("PLC.ModBusMaster").Create(server.Configuration(tags));
        await driver.ConnectAsync(server.Token);
        var results = await driver.ReadAsync(tags, server.Token);
        Assert.All(results, r => Assert.Equal(SampleQuality.Good, r.Quality));
        var batch = Assert.IsType<JsonElement>(results[0].Value);
        Assert.Equal(new[] { true, false, true }, batch.EnumerateArray().Select(e => e.GetBoolean()));
        Assert.True(Assert.IsType<bool>(results[1].Value));
        Assert.Equal(0, Assert.IsType<int>(results[2].Value));
        await server.Completion;
    }

    [Theory]
    [InlineData(ProtocolEndian.BigEndian, "0102030405060708")]
    [InlineData(ProtocolEndian.LittleEndian, "0807060504030201")]
    [InlineData(ProtocolEndian.BigEndianSwap, "0201040306050807")]
    [InlineData(ProtocolEndian.LittleEndianSwap, "0708050603040102")]
    public async Task RealOriginalUInt64WriteUsesSelectedEightByteOrderAndWaitsForEcho(ProtocolEndian endian, string wireHex)
    {
        await using var server = new Server(async (stream, token) =>
        {
            var request = await ReadRequest(stream, token);
            Assert.Equal(Convert.FromHexString("100008000408" + wireHex), request.AsSpan(7).ToArray());
            await stream.WriteAsync(Response(request, request.AsSpan(7, 5).ToArray()), token);
        });
        var tag = new TagConfiguration
        {
            Address = "8", ReadMethod = "HoldingRegisters", DataType = TagDataType.UInt64, Endian = endian, Writable = true
        };
        await using var driver = catalog.Get("PLC.ModBusMaster").Create(server.Configuration([tag]));
        await driver.ConnectAsync(server.Token);
        await driver.WriteAsync(tag, 0x0102030405060708UL, server.Token);
        await server.Completion;
    }

    private static async Task<byte[]> ReadRequest(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var request = new byte[6 + BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4))];
        header.CopyTo(request, 0);
        await stream.ReadExactlyAsync(request.AsMemory(7), cancellationToken);
        return request;
    }

    private static byte[] Response(byte[] request, byte[] pdu)
    {
        var response = new byte[7 + pdu.Length];
        request.AsSpan(0, 7).CopyTo(response);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), checked((ushort)(pdu.Length + 1)));
        pdu.CopyTo(response, 7);
        return response;
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        public CancellationToken Token => _timeout.Token;
        public Task Completion { get; }
        public Server(Func<NetworkStream, CancellationToken, Task> exchange)
        {
            _listener.Start();
            Completion = Task.Run(async () =>
            {
                using var peer = await _listener.AcceptTcpClientAsync(Token);
                await exchange(peer.GetStream(), Token);
            });
        }
        public DeviceConfiguration Configuration(List<TagConfiguration> tags) => new()
        {
            DriverId = "PLC.ModBusMaster", TimeoutMs = 1000, Tags = tags,
            Properties = new Dictionary<string, string>
            {
                ["IpAddress"] = "127.0.0.1", ["Port"] = ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
                ["MasterType"] = "Tcp", ["SlaveAddress"] = "1"
            }
        };
        public async ValueTask DisposeAsync()
        {
            _timeout.Cancel();
            _listener.Stop();
            try { await Completion; }
            catch (OperationCanceledException) { }
            _timeout.Dispose();
        }
    }
}
