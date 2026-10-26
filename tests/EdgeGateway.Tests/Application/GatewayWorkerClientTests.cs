using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EdgeGateway.Application;
using EdgeGateway.Model;
using EdgeGateway.WinForms;

namespace EdgeGateway.Tests.Application;

public sealed class GatewayWorkerClientTests
{
    public interface IAsyncProbe
    {
        ValueTask<StoreStatistics> ReadAsync(CancellationToken cancellationToken = default);
    }

    [Fact]
    public async Task GeneratedApplicationProxyReturnsGenericTaskResult()
    {
        await using var server = new PipeFixture("{\"OutboxCount\":17}");
        var client = server.CreateProxy<IGatewayApplication>();
        var result = await client.GetStatisticsAsync();
        Assert.Equal(17, result.OutboxCount);
        var request = await server.Request;
        Assert.Equal(nameof(IGatewayApplication.GetStatisticsAsync), request.GetProperty("Method").GetString());
        Assert.Equal("EdgeGateway.Application.IGatewayApplication", request.GetProperty("Contract").GetString());
    }

    [Fact]
    public async Task ResourceHistorySurvivesWorkerIpcRoundTrip()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var snapshot = new ServerResourceSnapshot(timestamp, "host", 8, 12, new(1000, 500), [], [])
        {
            History = [new(timestamp.AddSeconds(-1), 10, 49), new(timestamp, 12, 50)]
        };
        await using var server = new PipeFixture(JsonSerializer.Serialize(snapshot));
        var result = await server.CreateProxy<IGatewayApplication>().GetServerResourcesAsync();
        Assert.Equal(snapshot.History, result.History);
        Assert.Equal(snapshot.Timestamp, result.Timestamp);
    }

    [Fact]
    public async Task GeneratedProxyReturnsGenericValueTaskResult()
    {
        await using var server = new PipeFixture("{\"OutboxCount\":29}");
        var client = server.CreateProxy<IAsyncProbe>();
        Assert.Equal(29, (await client.ReadAsync()).OutboxCount);
    }

    [Fact]
    public async Task GenericTaskPropagatesWorkerErrorInsteadOfNullReference()
    {
        await using var server = new PipeFixture(null, "受控读取失败");
        var client = server.CreateProxy<IGatewayApplication>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStatisticsAsync());
        Assert.Contains("受控读取失败", error.Message);
    }

    [Fact]
    public async Task CancelledGenericTaskDoesNotConnectOrLaunchWorker()
    {
        var client = CreateProxy<IGatewayApplication>("unused-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatisticsAsync(new(true)));
    }

    [Fact]
    public async Task ChildDriverCatalogAndPlainTaskStillDispatch()
    {
        await using (var server = new PipeFixture("[]"))
        {
            var client = server.CreateProxy<IGatewayApplication>();
            Assert.Empty(client.Drivers.Drivers);
            var request = await server.Request;
            Assert.Equal("EdgeGateway.Application.IDriverCatalog", request.GetProperty("Contract").GetString());
        }
        await using (var server = new PipeFixture(null))
        {
            await server.CreateProxy<IGatewayApplication>().StopAsync();
            Assert.Equal(nameof(IGatewayApplication.StopAsync), (await server.Request).GetProperty("Method").GetString());
        }
    }

    private static T CreateProxy<T>(string pipeName) where T : class
    {
        var proxy = DispatchProxy.Create<T, GatewayWorkerClient>();
        typeof(GatewayWorkerClient).GetField("_pipeName", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(proxy, pipeName);
        // An accidental attempt to spawn a worker fails instead of starting the test runner.
        typeof(GatewayWorkerClient).GetField("_executable", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(proxy, "missing-worker.exe");
        return proxy;
    }

    private sealed class PipeFixture : IAsyncDisposable
    {
        private readonly string _name = "edge-worker-proxy-test-" + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        private readonly TaskCompletionSource<JsonElement> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _server;
        public Task<JsonElement> Request => _request.Task.WaitAsync(_timeout.Token);

        public PipeFixture(string? payload, string? error = null) => _server = ServeAsync(payload, error);
        public T CreateProxy<T>() where T : class => GatewayWorkerClientTests.CreateProxy<T>(_name);

        private async Task ServeAsync(string? payload, string? error)
        {
            // The client first sends a health request, then opens a separate command connection.
            for (var index = 0; index < 2; index++)
            {
                await using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_timeout.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(_timeout.Token) ?? throw new IOException("Missing request");
                using var document = JsonDocument.Parse(line);
                if (index == 0)
                {
                    Assert.Equal("health", document.RootElement.GetProperty("Contract").GetString());
                    await writer.WriteLineAsync("{\"Payload\":\"{}\",\"Error\":null}");
                }
                else
                {
                    _request.TrySetResult(document.RootElement.Clone());
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { Payload = payload, Error = error }));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _timeout.CancelAsync();
            try { await _server; } catch (OperationCanceledException) { }
            _timeout.Dispose();
        }
    }
}
