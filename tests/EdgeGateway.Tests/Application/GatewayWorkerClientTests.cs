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
    public async Task BatchEnableIdsAndResultSurviveWorkerIpc()
    {
        var id = Guid.NewGuid();
        await using var server = new PipeFixture("{\"Added\":[],\"Updated\":[\"" + id + "\"],\"Removed\":[],\"Restarted\":[],\"Stopped\":[]}");
        var result = await server.CreateProxy<IGatewayApplication>().SetDevicesEnabledAsync([id], false);
        Assert.Equal(id, Assert.Single(result.Updated));
        var request = await server.Request;
        Assert.Equal(nameof(IGatewayApplication.SetDevicesEnabledAsync), request.GetProperty("Method").GetString());
        Assert.Equal(id, request.GetProperty("Arguments")[0][0].GetGuid());
        Assert.False(request.GetProperty("Arguments")[1].GetBoolean());
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
    public async Task SeparatedLogQueryAndStructuredSampleSurviveWorkerIpc()
    {
        var now = DateTimeOffset.UtcNow;
        var query = new GatewayLogQuery(now.AddMinutes(-1), now) { Category = GatewayLogCategory.Samples, Device = "设备", Tag = "点位", Quality = SampleQuality.Bad };
        var sample = new GatewayLogSample(Guid.NewGuid(), "设备", Guid.NewGuid(), "点位", "DB1.0", "", "", "", SampleQuality.Bad, "读取超时");
        var entry = new GatewayLogFileEntry(now, GatewayLogLevel.Information, "sample", "", "data/sample.log", "{}") { Sample = sample };
        await using var server = new PipeFixture(JsonSerializer.Serialize(new GatewayLogQueryResult(query, [entry], 20, 0, false, [])));
        var result = await server.CreateProxy<IGatewayApplication>().QueryLogFilesAsync(query);
        Assert.Equal(query, result.Query); Assert.Equal(sample, Assert.Single(result.Entries).Sample);
        var arguments = (await server.Request).GetProperty("Arguments");
        Assert.Equal((int)GatewayLogCategory.Samples, arguments[0].GetProperty("Category").GetInt32());
    }

    [Fact]
    public async Task SampleLogHistoryPreservesPrecisionQualityAndWarningsAcrossWorkerIpc()
    {
        var time = DateTimeOffset.Now;
        var sample = new TelemetrySample(Guid.NewGuid(), "设备", Guid.NewGuid(), "点位", "0",
            long.MaxValue, null, SampleQuality.Bad, time, "℃", "超时");
        var snapshot = new SampleLogHistoryResult([sample], 1234, 2, true, ["扫描受限"]);
        await using var server = new PipeFixture(JsonSerializer.Serialize(snapshot));
        var query = new HistoryQuery(sample.DeviceId, sample.TagId, time.AddMinutes(-1), time, 20);
        var result = await server.CreateProxy<IGatewayApplication>().QuerySampleLogHistoryAsync(query, 16);
        var read = Assert.Single(result.Samples);
        Assert.Equal(long.MaxValue, Assert.IsType<long>(read.RawValue));
        Assert.Null(read.Value); Assert.Equal(SampleQuality.Bad, read.Quality);
        Assert.Equal(time, read.Timestamp); Assert.Equal("超时", read.Error);
        Assert.True(result.IsPartial); Assert.Equal(snapshot.Warnings, result.Warnings);
        var request = await server.Request;
        Assert.Equal(nameof(IGatewayApplication.QuerySampleLogHistoryAsync), request.GetProperty("Method").GetString());
        Assert.Equal(sample.DeviceId, request.GetProperty("Arguments")[0].GetProperty("DeviceId").GetGuid());
        Assert.Equal(16, request.GetProperty("Arguments")[1].GetInt32());
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

    [Fact]
    public void EarlyWorkerExitReportsOnlyNewSchemaFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "edge-worker-error-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(path, "[old] System.IO.InvalidDataException: 配置数据库结构版本 3 不受支持\n");
            var offset = new FileInfo(path).Length;
            File.AppendAllText(path, "[new] System.IO.InvalidDataException: 配置数据库结构版本 4 不受支持；当前需要版本 5。\n");
            var message = GatewayWorkerClient.WorkerExitMessage(path, offset);
            Assert.Contains("版本 4", message);
            Assert.Contains("版本 5", message);
            Assert.DoesNotContain("版本 3", message);
            Assert.Contains("请先备份原库", message);
        }
        finally { File.Delete(path); }
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
