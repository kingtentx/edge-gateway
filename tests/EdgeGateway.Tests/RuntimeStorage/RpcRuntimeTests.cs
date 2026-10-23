using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class RpcRuntimeTests
{
    [Fact]
    public async Task RemoteWritesAreDeniedByDefaultAndReadPerformsFreshDriverOperation()
    {
        await using var harness = new RpcHarness();
        await harness.Start();
        var denied = await harness.Request("cmd-1", "write", 15);
        Assert.Equal("WriteDenied", denied.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(0, harness.Factory.Writes);
        harness.Factory.Value = 29;
        var before = harness.Factory.Reads;
        var read = await harness.Request("read-1", "read");
        Assert.True(read.GetProperty("data").GetProperty("success").GetBoolean());
        Assert.True(harness.Factory.Reads > before);
        Assert.Equal(29, read.GetProperty("data").GetProperty("data").GetProperty("sample").GetProperty("Value").GetInt32());
        Assert.Equal(0, harness.Factory.Writes);
    }

    [Fact]
    public async Task VerifiedWriteDuplicatesReplayAndConflictingReuseDoesNotActuateAgain()
    {
        await using var harness = new RpcHarness(allowWrites: true);
        await harness.Start();
        var first = await harness.Request("write-1", "write", 15);
        Assert.Equal("Matched", first.GetProperty("data").GetProperty("status").GetString());
        Assert.True(first.GetProperty("data").GetProperty("success").GetBoolean());
        var duplicate = await harness.Request("write-1", "write", 15);
        Assert.Equal(first.GetRawText(), duplicate.GetRawText());
        var conflict = await harness.Request("write-1", "write", 16);
        Assert.Equal("RequestIdConflict", conflict.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(1, harness.Factory.Writes);
        Assert.Equal(15, Convert.ToInt32(harness.Factory.Value));
        Assert.Equal(1, harness.Runtime.GetRpcStatus().ReplayedRequests);
        Assert.Contains(harness.Log.GetEntries(), entry => entry.Message.Contains("write-1") && entry.Message.Contains("持久"));
    }

    [Fact]
    public async Task RequestIdsCannotChangeMethodsWhileEvidenceExists()
    {
        await using var harness = new RpcHarness(allowWrites: true);
        await harness.Start();
        Assert.True((await harness.Request("read-id", "read")).GetProperty("data").GetProperty("success").GetBoolean());
        Assert.Equal("RequestIdConflict", (await harness.Request("read-id", "write", 7)).GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(0, harness.Factory.Writes);
        Assert.Equal("Matched", (await harness.Request("write-id", "write", 11)).GetProperty("data").GetProperty("status").GetString());
        var reads = harness.Factory.Reads;
        Assert.Equal("RequestIdConflict", (await harness.Request("write-id", "read")).GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(reads, harness.Factory.Reads);
        Assert.Equal(1, harness.Factory.Writes);
    }

    [Fact]
    public async Task DurableWriteResultReplaysAfterNewRuntimeWithoutRepeatedDeviceWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-rpc-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = RpcHarness.Configuration(true);
            JsonElement first;
            await using (var harness = new RpcHarness(configuration: config, directory: directory))
            {
                await harness.Start();
                first = await harness.Request("persistent-1", "write", 23);
                Assert.Equal(1, harness.Factory.Writes);
            }
            await using (var restarted = new RpcHarness(configuration: config, directory: directory))
            {
                await restarted.Start();
                var replay = await restarted.Request("persistent-1", "write", 23);
                Assert.Equal(first.GetRawText(), replay.GetRawText());
                Assert.Equal(0, restarted.Factory.Writes);
                var reads = restarted.Factory.Reads;
                Assert.Equal("RequestIdConflict", (await restarted.Request("persistent-1", "read")).GetProperty("data").GetProperty("status").GetString());
                Assert.Equal(reads, restarted.Factory.Reads);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("cloud")]
    [InlineData("account")]
    [InlineData("credentialVariable")]
    public async Task DistinctConfiguredCloudIdentitiesDoNotReplayAnotherGatewayResult(string changedIdentity)
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-rpc-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = RpcHarness.Configuration(true);
            if (changedIdentity == "cloud") { config.Mqtt.Platform = MqttPlatform.ThingsPanel; config.Mqtt.PlatformGatewayId = "cloud-a"; }
            if (changedIdentity == "account") config.Mqtt.Username = "account-a";
            if (changedIdentity == "credentialVariable") config.Mqtt.UsernameEnvironmentVariable = "EDGE_ACCOUNT_A";
            JsonElement first;
            await using (var harness = new RpcHarness(configuration: config, directory: directory))
            {
                await harness.Start();
                first = await harness.Request("identity-1", "write", 23);
                Assert.Equal("Matched", first.GetProperty("data").GetProperty("status").GetString());
                Assert.Equal(1, harness.Factory.Writes);
            }
            if (changedIdentity == "cloud") config.Mqtt.PlatformGatewayId = "cloud-b";
            if (changedIdentity == "account") config.Mqtt.Username = "account-b";
            if (changedIdentity == "credentialVariable") config.Mqtt.UsernameEnvironmentVariable = "EDGE_ACCOUNT_B";
            await using (var differentIdentity = new RpcHarness(configuration: config, directory: directory))
            {
                await differentIdentity.Start();
                var response = await differentIdentity.Request("identity-1", "write", 23);
                Assert.Equal("Matched", response.GetProperty("data").GetProperty("status").GetString());
                Assert.Equal(1, differentIdentity.Factory.Writes);
                Assert.NotEqual(first.GetProperty("data").GetProperty("commandId").GetString(), response.GetProperty("data").GetProperty("commandId").GetString());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RotatingCredentialValueKeepsDurableReplayIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-rpc-rotation-" + Guid.NewGuid().ToString("N"));
        var variable = "EDGE_RPC_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        Directory.CreateDirectory(directory);
        try
        {
            var config = RpcHarness.Configuration(true);
            config.Mqtt.UsernameEnvironmentVariable = variable;
            Environment.SetEnvironmentVariable(variable, "test-token-before");
            JsonElement first;
            await using (var harness = new RpcHarness(configuration: config, directory: directory))
            {
                await harness.Start();
                first = await harness.Request("rotation-1", "write", 31);
                Assert.Equal(1, harness.Factory.Writes);
            }
            Environment.SetEnvironmentVariable(variable, "test-token-after");
            await using (var rotated = new RpcHarness(configuration: config, directory: directory))
            {
                await rotated.Start();
                var replay = await rotated.Request("rotation-1", "write", 31);
                Assert.Equal(first.GetRawText(), replay.GetRawText());
                Assert.Equal(0, rotated.Factory.Writes);
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RemoteWriteRequiresWritableTagAndRunningTargetAndRejectsOtherMethods()
    {
        await using var harness = new RpcHarness(allowWrites: true);
        harness.Config.Devices[0].Tags[0].Writable = false;
        var other = new DeviceConfiguration { Name = "other", DriverId = "rpc-test", Tags = [new() { Name = "spare" }] };
        harness.Config.Devices.Add(other);
        harness.Runtime.Configure(harness.Config);
        await harness.Start();
        Assert.Equal("WriteDenied", (await harness.Request("readonly-1", "write", 1)).GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("MethodNotAllowed", (await harness.Request("restart-1", "restart")).GetProperty("data").GetProperty("status").GetString());
        await harness.Runtime.StopDeviceAsync(harness.Config.Devices[0].Id);
        Assert.Equal("DeviceNotRunning", (await harness.Request("stopped-1", "write", 1)).GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(0, harness.Factory.Writes);
    }

    [Fact]
    public async Task PendingJournalClaimSurvivesRestartAndCapacityDoesNotEraseOlderEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-rpc-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "gateway.db");
        try
        {
            await using (var store = new SqliteGatewayStore(path))
            {
                await store.InitializeAsync();
                Assert.True((await store.ClaimRpcCommandAsync("pending", "first-fingerprint", 1)).Created);
            }
            await using (var store = new SqliteGatewayStore(path))
            {
                await store.InitializeAsync();
                var claim = await store.ClaimRpcCommandAsync("pending", "different-fingerprint", 1);
                Assert.False(claim.Created); Assert.Equal("first-fingerprint", claim.Entry.Fingerprint); Assert.Null(claim.Entry.ResponsePayload);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClaimRpcCommandAsync("new", "fingerprint", 1));
                await store.CompleteRpcCommandAsync("pending", "edge/reply", "{\"status\":\"Indeterminate\"}");
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteRpcCommandAsync("pending", "edge/reply", "{}"));
                var completed = await store.ClaimRpcCommandAsync("pending", "first-fingerprint", 1);
                Assert.Equal("{\"status\":\"Indeterminate\"}", completed.Entry.ResponsePayload);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PendingWriteClaimReturnsIndeterminateWithoutTouchingDriver()
    {
        await using var harness = new RpcHarness(allowWrites: true);
        await harness.Start();
        var device = harness.Config.Devices[0];
        var tag = device.Tags[0];
        var config = harness.Config;
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var key = Hash(JsonSerializer.Serialize(new[] { "Generic", config.Mqtt.Host, config.Mqtt.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            config.Mqtt.ClientId, config.GatewayId, config.Mqtt.PlatformGatewayId, $"username:{config.Mqtt.Username}", device.Id.ToString(), "pending-1" }));
        await harness.Store.ClaimRpcCommandAsync(key, Hash($"write\n{tag.Id}\n17"), 10000);
        var response = await harness.Request("pending-1", "write", 17);
        Assert.Equal("Indeterminate", response.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(0, harness.Factory.Writes);
    }

    private sealed class RpcHarness : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly bool _ownsDirectory;
        public GatewayConfiguration Config { get; }
        public RpcFactory Factory { get; } = new();
        public GatewayLog Log { get; } = new();
        public RpcPublisher Publisher { get; } = new();
        public SqliteGatewayStore Store { get; }
        public GatewayRuntime Runtime { get; }
        public RpcHarness(bool allowWrites = false, GatewayConfiguration? configuration = null, string? directory = null)
        {
            Config = configuration ?? Configuration(allowWrites);
            _directory = directory ?? Path.Combine(Path.GetTempPath(), "edge-rpc-test-" + Guid.NewGuid().ToString("N"));
            _ownsDirectory = directory is null;
            Directory.CreateDirectory(_directory);
            Store = new SqliteGatewayStore(Path.Combine(_directory, "gateway.db"));
            Runtime = new GatewayRuntime(Config, new DriverRegistry([Factory], Log), Store, Publisher, Log);
        }
        public static GatewayConfiguration Configuration(bool allowWrites) => new()
        {
            PublishIntervalMs = 3600000,
            Mqtt = new() { Enabled = true, RpcEnabled = true, AllowRemoteWrite = allowWrites, RpcTimeoutMs = 2000 },
            Devices = [new() { Name = "pump", DriverId = "rpc-test", PollIntervalMs = 3600000, TimeoutMs = 1000,
                Tags = [new() { Name = "speed", Writable = true, DataType = TagDataType.UInt16 }] }]
        };
        public async Task Start()
        {
            await Runtime.StartAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Runtime.GetLatest().Any(sample => sample.Quality == SampleQuality.Good)) await Task.Delay(10, deadline.Token);
        }
        public async Task<JsonElement> Request(string id, string method, int? value = null)
        {
            var command = new { method, @params = new { tag = "speed", value } };
            var payload = Config.Mqtt.Platform == MqttPlatform.ThingsPanel
                ? JsonSerializer.Serialize(new { sub_device_data = new Dictionary<string, object> { ["pump"] = command } })
                : JsonSerializer.Serialize(new { id, device = "pump", method, @params = new { tag = "speed", value } });
            var topic = Config.Mqtt.Platform == MqttPlatform.ThingsPanel ? $"gateway/command/{Config.Mqtt.PlatformGatewayId}/{id}" : "edge/edge-001/rpc/request";
            Publisher.Incoming.Writer.TryWrite(new(topic, payload, DateTimeOffset.UtcNow));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var reply = await Publisher.Replies.Reader.ReadAsync(deadline.Token);
            return JsonDocument.Parse(reply.Payload).RootElement.Clone();
        }
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync(); await Store.DisposeAsync(); await Publisher.DisposeAsync();
            if (_ownsDirectory) Directory.Delete(_directory, true);
        }
    }

    private sealed class RpcFactory : IDriverFactory
    {
        public DriverDescriptor Descriptor => new("rpc-test", "RPC test", "test", true);
        public object Value = 2;
        public int Writes;
        public int Reads;
        public IReadOnlyList<string> Validate(DeviceConfiguration configuration) => [];
        public IDeviceDriver Create(DeviceConfiguration configuration) => new RpcDriver(this);
        private sealed class RpcDriver(RpcFactory factory) : IDeviceDriver
        {
            public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken)
            { Interlocked.Increment(ref factory.Reads); return Task.FromResult<IReadOnlyList<TagReadResult>>(tags.Select(tag => new TagReadResult(tag.Id, factory.Value)).ToArray()); }
            public Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref factory.Writes); factory.Value = value; return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RpcPublisher : ITelemetryPublisher, IMqttRpcTransport
    {
        public Channel<MqttInboundMessage> Incoming { get; } = Channel.CreateUnbounded<MqttInboundMessage>();
        public Channel<MqttOutboundMessage> Replies { get; } = Channel.CreateUnbounded<MqttOutboundMessage>();
        public bool IsConnected { get; private set; }
        public long ConnectionVersion => 1;
        public long DroppedMessages => 0;
        public Task EnsureConnectedAsync(MqttConfiguration settings, IReadOnlyList<string> subscriptionTopics, CancellationToken cancellationToken)
        { IsConnected = true; return Task.CompletedTask; }
        public ValueTask<MqttInboundMessage> ReceiveAsync(CancellationToken cancellationToken) => Incoming.Reader.ReadAsync(cancellationToken);
        public Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken)
        { Replies.Writer.TryWrite(new(topic, payload)); return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
