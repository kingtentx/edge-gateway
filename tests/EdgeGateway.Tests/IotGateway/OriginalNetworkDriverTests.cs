using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.IotGateway;

public sealed class OriginalNetworkDriverTests(CatalogFixture catalog) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task MtConnectProbesRealHttpAgentThenReadsRequestedDataItemThroughOriginalClient()
    {
        await using var agent = new MtConnectAgent(200, ProbeDocument, StreamsDocument("spindle-speed", "1234.5"));
        var tag = MtConnectTag("spindle-speed");
        await using var driver = catalog.Get("CNC.MTConnect").Create(agent.Configuration([tag]));

        await driver.ConnectAsync(agent.Token);
        var sample = Assert.Single(await driver.ReadAsync([tag], agent.Token));

        Assert.Equal(SampleQuality.Good, sample.Quality);
        Assert.Equal(1234.5, Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture));
        Assert.Null(sample.Error);
        Assert.Contains(agent.Requests, request => request == "GET /probe");
        Assert.Contains(agent.Requests, request => request.StartsWith("GET /current", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(200, "<html>not an MTConnect agent</html>")]
    [InlineData(200, "<MTConnectDevices>")]
    [InlineData(503, "<MTConnectDevices />")]
    public async Task MtConnectInvalidProbeCannotReportSuccessfulConnection(int status, string body)
    {
        await using var agent = new MtConnectAgent(status, body, StreamsDocument("spindle-speed", "1234.5"));
        var tag = MtConnectTag("spindle-speed");
        await using var driver = catalog.Get("CNC.MTConnect").Create(agent.Configuration([tag]));

        await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(agent.Token));
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync([tag], agent.Token));

        Assert.Equal(new[] { "GET /probe" }, agent.Requests.ToArray());
    }

    [Theory]
    [InlineData("missing-item", "1234.5")]
    [InlineData("spindle-speed", "UNAVAILABLE")]
    public async Task MtConnectMissingOrUnavailableItemCannotBecomeGood(string requestedId, string value)
    {
        await using var agent = new MtConnectAgent(200, ProbeDocument, StreamsDocument("spindle-speed", value));
        var tag = MtConnectTag(requestedId);
        await using var driver = catalog.Get("CNC.MTConnect").Create(agent.Configuration([tag]));

        await driver.ConnectAsync(agent.Token);
        var sample = Assert.Single(await driver.ReadAsync([tag], agent.Token));

        Assert.True(sample.Quality == SampleQuality.Bad,
            $"Expected Bad for requested id '{requestedId}', got {sample.Quality} / '{sample.Value}'. HTTP requests: {string.Join("; ", agent.Requests)}");
        Assert.Null(sample.Value);
        Assert.False(string.IsNullOrWhiteSpace(sample.Error));
        Assert.Contains(agent.Requests, request => request.StartsWith("GET /current", StringComparison.Ordinal));
    }

    private static TagConfiguration MtConnectTag(string id) => new()
    {
        Name = id, Address = id, ReadMethod = "ReadById", DataType = TagDataType.Any
    };

    private const string ProbeDocument = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MTConnectDevices xmlns="urn:mtconnect.org:MTConnectDevices:1.3">
          <Header creationTime="2026-10-22T08:00:00Z" sender="loopback-agent" instanceId="1" version="1.3" bufferSize="1024" />
          <Devices>
            <Device id="machine" name="TestMachine" uuid="loopback-machine">
              <DataItems>
                <DataItem id="spindle-speed" name="spindleSpeed" category="SAMPLE" type="SPINDLE_SPEED" units="REVOLUTION/MINUTE" />
              </DataItems>
            </Device>
          </Devices>
        </MTConnectDevices>
        """;

    private static string StreamsDocument(string id, string value) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:1.3">
          <Header creationTime="2026-10-22T08:00:00Z" sender="loopback-agent" instanceId="1" version="1.3" bufferSize="1024" firstSequence="1" lastSequence="1" nextSequence="2" />
          <Streams>
            <DeviceStream name="TestMachine" uuid="loopback-machine">
              <ComponentStream component="Device" name="TestMachine" componentId="machine">
                <Samples>
                  <SpindleSpeed dataItemId="{{id}}" name="spindleSpeed" sequence="1" timestamp="2026-10-22T08:00:00Z">{{value}}</SpindleSpeed>
                </Samples>
              </ComponentStream>
            </DeviceStream>
          </Streams>
        </MTConnectStreams>
        """;

    /// <summary>A real HTTP/1.1 endpoint that closes after each response; only loopback is bound.</summary>
    private sealed class MtConnectAgent : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
        private readonly Task _serve;
        private readonly int _probeStatus;
        private readonly string _probeBody, _currentBody;
        public ConcurrentQueue<string> Requests { get; } = new();
        public CancellationToken Token => _deadline.Token;

        public MtConnectAgent(int probeStatus, string probeBody, string currentBody)
        {
            _probeStatus = probeStatus; _probeBody = probeBody; _currentBody = currentBody;
            _listener.Start();
            _serve = ServeAsync();
        }

        public DeviceConfiguration Configuration(List<TagConfiguration> tags) => new()
        {
            DriverId = "CNC.MTConnect", TimeoutMs = 2000, Tags = tags,
            Properties = new Dictionary<string, string>
            {
                ["Uri"] = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)
            }
        };

        private async Task ServeAsync()
        {
            while (!Token.IsCancellationRequested)
            {
                using var peer = await _listener.AcceptTcpClientAsync(Token);
                await using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var line = await reader.ReadLineAsync(Token);
                if (line is null) continue;
                var parts = line.Split(' ');
                if (parts.Length != 3) throw new InvalidDataException("Malformed test-agent HTTP request: " + line);
                Requests.Enqueue(parts[0] + " " + parts[1]);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(Token))) { }
                var path = new Uri("http://127.0.0.1" + parts[1]).AbsolutePath;
                var status = path == "/probe" ? _probeStatus : path == "/current" ? 200 : 404;
                var body = path == "/probe" ? _probeBody : path == "/current" ? _currentBody : "Unknown test-agent path";
                var bytes = Encoding.UTF8.GetBytes(body);
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/xml; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, Token);
                await stream.WriteAsync(bytes, Token);
                await stream.FlushAsync(Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _deadline.Cancel();
            _listener.Stop();
            try { await _serve; }
            catch (OperationCanceledException) when (_deadline.IsCancellationRequested) { }
            catch (SocketException) when (_deadline.IsCancellationRequested) { }
            finally { _deadline.Dispose(); }
        }
    }
}
