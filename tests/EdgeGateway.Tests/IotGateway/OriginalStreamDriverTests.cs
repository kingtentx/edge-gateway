using System.Globalization;
using System.Net;
using System.Net.Sockets;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.IotGateway;

public sealed class OriginalStreamDriverTests(CatalogFixture catalog) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task OriginalMockHandlesFragmentedFrameSignedByteAndRejectsStaleSecondRead()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new StreamServer(async (stream, token) =>
        {
            byte[] command = new byte[4];
            await stream.ReadExactlyAsync(command, token);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, command);
            await stream.WriteAsync(new byte[] { 8, 255 }, token);
            await Task.Delay(70, token);
            await stream.WriteAsync(new byte[] { 0, 0, 0, 0, 0, 0 }, token);
            await stream.ReadExactlyAsync(command, token);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, command);
            // Leave the socket open but provide no answer to request two.
            await finished.Task.WaitAsync(token);
        });
        var tag = new TagConfiguration { Address = "1", ReadMethod = "Read", DataType = TagDataType.Int8 };
        await using var driver = catalog.Get("Mock.TcpClient").Create(server.Configuration("Mock.TcpClient", [tag]));
        await driver.ConnectAsync(server.Token);
        var first = Assert.Single(await driver.ReadAsync([tag], server.Token));
        Assert.Equal(SampleQuality.Good, first.Quality);
        Assert.Equal((sbyte)-1, Assert.IsType<sbyte>(first.Value));
        var second = Assert.Single(await driver.ReadAsync([tag], server.Token));
        Assert.Equal(SampleQuality.Bad, second.Quality);
        Assert.Null(second.Value);
        finished.TrySetResult();
        await server.Completion;
    }

    [Fact]
    public async Task OriginalToledoReassemblesContinuousFramesAndTimesOutAfterStreamStops()
    {
        var stopPublishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new StreamServer(async (stream, token) =>
        {
            // Real Toledo mode is a continuous TCP stream with fixed-length STX/CR frames.
            while (!stopPublishing.Task.IsCompleted)
            {
                await stream.WriteAsync(new byte[] { 2, 255 }, token);
                await Task.Delay(35, token);
                await stream.WriteAsync(new byte[] { 3, 4, 5, 6, 7, 13 }, token);
                await Task.Delay(35, token);
            }
            publishingStopped.TrySetResult();
            await finished.Task.WaitAsync(token);
        });
        var tag = new TagConfiguration { Address = "1", ReadMethod = "Read", DataType = TagDataType.Int8 };
        var configuration = server.Configuration("Other.Toledo", [tag]);
        configuration.Properties["DataBits"] = "8";
        await using var driver = catalog.Get("Other.Toledo").Create(configuration);
        await driver.ConnectAsync(server.Token);
        var first = Assert.Single(await driver.ReadAsync([tag], server.Token));
        Assert.Equal(SampleQuality.Good, first.Quality);
        Assert.Equal((sbyte)-1, Assert.IsType<sbyte>(first.Value));
        stopPublishing.TrySetResult();
        await publishingStopped.Task.WaitAsync(server.Token);
        // Let the client's normal receive callback drain the last already-published chunk.
        await Task.Delay(200, server.Token);
        var second = Assert.Single(await driver.ReadAsync([tag], server.Token));
        Assert.Equal(SampleQuality.Bad, second.Quality);
        Assert.Null(second.Value);
        finished.TrySetResult();
        await server.Completion;
    }

    private sealed class StreamServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(12));
        public CancellationToken Token => _timeout.Token;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public Task Completion { get; }

        public StreamServer(Func<NetworkStream, CancellationToken, Task> exchange)
        {
            _listener.Start();
            Completion = Task.Run(async () =>
            {
                using var peer = await _listener.AcceptTcpClientAsync(Token);
                await exchange(peer.GetStream(), Token);
            });
        }

        public DeviceConfiguration Configuration(string driverId, List<TagConfiguration> tags) => new()
        {
            DriverId = driverId,
            TimeoutMs = 1000,
            Tags = tags,
            Properties = new Dictionary<string, string>
            {
                ["IpAddress"] = "127.0.0.1",
                ["Port"] = Port.ToString(CultureInfo.InvariantCulture),
            },
        };

        public async ValueTask DisposeAsync()
        {
            _timeout.Cancel();
            _listener.Stop();
            try { await Completion; }
            catch (OperationCanceledException) when (_timeout.IsCancellationRequested) { }
            _timeout.Dispose();
        }
    }
}
