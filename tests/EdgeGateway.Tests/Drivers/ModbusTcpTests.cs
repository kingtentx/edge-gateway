using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using EdgeGateway.Driver.Modbus;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.Drivers;

public sealed class ModbusTcpTests
{
    [Fact]
    public async Task Fragmented_tcp_responses_support_all_read_areas()
    {
        await using var server = new TcpServer(async (stream, token) =>
        {
            for (byte function = 1; function <= 4; function++)
            {
                var request = await ReadRequestAsync(stream, token);
                Assert.Equal(function, request[7]);
                Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)));
                Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10)));
                byte[] pdu = function <= 2 ? [function, 1, 1] : [function, 2, 0, 42];
                var frame = Response(request, pdu);
                // Deliberately split MBAP and data into single-byte writes.
                foreach (var value in frame)
                {
                    await stream.WriteAsync(new[] { value }, token);
                    await Task.Delay(1, token);
                }
            }
        });
        var tags = Enum.GetValues<ModbusArea>().Select(area => new TagConfiguration
        {
            Address = "0", Area = area, DataType = area is ModbusArea.Coil or ModbusArea.DiscreteInput ? TagDataType.Boolean : TagDataType.UInt16
        }).ToList();
        await using var driver = new ModbusTcpDriverFactory().Create(server.Configuration(tags));
        await driver.ConnectAsync(server.Token);
        var values = await driver.ReadAsync(tags, server.Token);
        Assert.Equal(4, values.Count);
        Assert.True(Assert.IsType<bool>(values[0].Value));
        Assert.True(Assert.IsType<bool>(values[1].Value));
        Assert.Equal((ushort)42, Assert.IsType<ushort>(values[2].Value));
        Assert.Equal((ushort)42, Assert.IsType<ushort>(values[3].Value));
        await server.Completion;
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("protocol")]
    [InlineData("unit")]
    [InlineData("length")]
    [InlineData("function")]
    [InlineData("bytecount")]
    [InlineData("exceptionlength")]
    public async Task Malformed_frames_are_rejected_and_connection_is_closed(string defect)
    {
        await using var server = new TcpServer(async (stream, token) =>
        {
            var request = await ReadRequestAsync(stream, token);
            var response = Response(request, [3, 2, 0, 42]);
            switch (defect)
            {
                case "transaction": response[1] ^= 1; break;
                case "protocol": response[3] = 1; break;
                case "unit": response[6] = 2; break;
                case "length": response[4] = 0xFF; response[5] = 0xFF; break;
                case "function": response[7] = 4; break;
                case "bytecount": response[8] = 1; break;
                case "exceptionlength": response = Response(request, [0x83, 2, 0]); break;
            }
            await stream.WriteAsync(response, token);
            await AssertPeerClosedAsync(stream, token);
        });
        var tag = new TagConfiguration { Address = "0" };
        await using var driver = new ModbusTcpDriverFactory().Create(server.Configuration([tag]));
        await driver.ConnectAsync(server.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => driver.ReadAsync([tag], server.Token));
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync([tag], server.Token));
        await server.Completion;
    }

    [Fact]
    public async Task Device_exception_marks_point_bad_and_preserves_usable_connection()
    {
        await using var server = new TcpServer(async (stream, token) =>
        {
            var first = await ReadRequestAsync(stream, token);
            await stream.WriteAsync(Response(first, [0x83, 2]), token);
            var second = await ReadRequestAsync(stream, token);
            await stream.WriteAsync(Response(second, [3, 2, 0, 7]), token);
        });
        var tags = new List<TagConfiguration> { new() { Address = "0" }, new() { Address = "1" } };
        await using var driver = new ModbusTcpDriverFactory().Create(server.Configuration(tags));
        await driver.ConnectAsync(server.Token);
        var values = await driver.ReadAsync(tags, server.Token);
        Assert.Equal(SampleQuality.Bad, values[0].Quality);
        Assert.Contains("0x02", values[0].Error);
        Assert.Equal(SampleQuality.Good, values[1].Quality);
        Assert.Equal((ushort)7, Assert.IsType<ushort>(values[1].Value));
        await server.Completion;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_cancellation_close_uncertain_exchange(bool cancel)
    {
        await using var server = new TcpServer(async (stream, token) =>
        {
            await ReadRequestAsync(stream, token);
            await AssertPeerClosedAsync(stream, token);
        });
        var tag = new TagConfiguration { Address = "0" };
        var configuration = server.Configuration([tag]);
        configuration.TimeoutMs = cancel ? 3000 : 100;
        await using var driver = new ModbusTcpDriverFactory().Create(configuration);
        await driver.ConnectAsync(server.Token);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.CancelAfter(100);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.ReadAsync([tag], cancellation.Token));
        else
            await Assert.ThrowsAsync<TimeoutException>(() => driver.ReadAsync([tag], server.Token));
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync([tag], server.Token));
        await server.Completion;
    }

    [Fact]
    public async Task Single_and_multiple_writes_use_correct_functions_and_payloads()
    {
        await using var server = new TcpServer(async (stream, token) =>
        {
            var expected = new[] { "050002FF00", "0600041234", "10000800020403040102" };
            foreach (var hex in expected)
            {
                var request = await ReadRequestAsync(stream, token);
                Assert.Equal(Convert.FromHexString(hex), request.AsSpan(7).ToArray());
                await stream.WriteAsync(Response(request, request.AsSpan(7, 5).ToArray()), token);
            }
        });
        var tags = new List<TagConfiguration>
        {
            new() { Address = "2", Area = ModbusArea.Coil, DataType = TagDataType.Boolean, Writable = true },
            new() { Address = "4", DataType = TagDataType.UInt16, Writable = true },
            new() { Address = "8", DataType = TagDataType.UInt32, ByteOrder = RegisterByteOrder.CDAB, Writable = true }
        };
        await using var driver = new ModbusTcpDriverFactory().Create(server.Configuration(tags));
        await driver.ConnectAsync(server.Token);
        await driver.WriteAsync(tags[0], true, server.Token);
        await driver.WriteAsync(tags[1], 0x1234, server.Token);
        await driver.WriteAsync(tags[2], 0x01020304u, server.Token);
        await server.Completion;
    }

    [Fact]
    public async Task Mismatched_write_echo_is_rejected_without_automatic_retry()
    {
        await using var server = new TcpServer(async (stream, token) =>
        {
            var request = await ReadRequestAsync(stream, token);
            var echo = request.AsSpan(7, 5).ToArray();
            echo[4] ^= 1;
            await stream.WriteAsync(Response(request, echo), token);
            await AssertPeerClosedAsync(stream, token);
        });
        var tag = new TagConfiguration { Address = "0", Writable = true };
        await using var driver = new ModbusTcpDriverFactory().Create(server.Configuration([tag]));
        await driver.ConnectAsync(server.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => driver.WriteAsync(tag, 42, server.Token));
        await server.Completion;
    }

    private static async Task AssertPeerClosedAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        }
        catch (IOException ex) when (!token.IsCancellationRequested &&
            ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
            // Rejecting MBAP before draining the remaining PDU can produce TCP RST on
            // Windows. Both RST and orderly EOF prove the driver closed the connection.
        }
        catch (SocketException ex) when (!token.IsCancellationRequested &&
            ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            // Keep this explicit for stream implementations exposing SocketException directly.
        }
        token.ThrowIfCancellationRequested();
    }

    private static async Task<byte[]> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        var request = new byte[6 + length];
        header.CopyTo(request, 0);
        await stream.ReadExactlyAsync(request.AsMemory(7), token);
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

    private sealed class TcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        public CancellationToken Token => _timeout.Token;
        public Task Completion { get; }
        public TcpServer(Func<NetworkStream, CancellationToken, Task> process)
        {
            _listener.Start();
            Completion = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync(Token);
                await process(client.GetStream(), Token);
            });
        }
        public DeviceConfiguration Configuration(List<TagConfiguration> tags) => new()
        {
            DriverId = "modbus-tcp", Host = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
            UnitId = 1, TimeoutMs = 3000, Tags = tags
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
