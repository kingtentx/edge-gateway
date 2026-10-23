using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class PlatformSwitchTests
{
    [Fact]
    public async Task PendingPayloadPreventsProtocolSwitchBeforeAtomicSaveAndAllowsSwitchAfterDrain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-platform-change-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new GatewayConfiguration();
            var configurationStore = new JsonConfigurationStore(Path.Combine(directory, "gateway.json"));
            await configurationStore.SaveAsync(configuration);
            await using var store = new SqliteGatewayStore(Path.Combine(directory, "gateway.db"));
            await store.InitializeAsync();
            Assert.True(await store.EnqueueAsync("edge/edge-001/telemetry", "{\"messageId\":\"old\"}", 100));
            await using var publisher = new MqttTelemetryPublisher();
            var log = new GatewayLog();
            await using var runtime = new GatewayRuntime(configuration, new DriverRegistry([], log), store, publisher, log);
            var candidate = await configurationStore.LoadAsync();
            candidate.Mqtt.Platform = MqttPlatform.ThingsBoard;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ApplyConfigurationAsync(candidate, configurationStore));
            Assert.Contains("补传", error.Message);
            Assert.Equal(MqttPlatform.Generic, (await configurationStore.LoadAsync()).Mqtt.Platform);
            var pending = Assert.Single(await store.ReadOutboxAsync(10));
            Assert.Equal("{\"messageId\":\"old\"}", pending.Payload);
            await store.AcknowledgeAsync(pending.Id);
            await runtime.ApplyConfigurationAsync(candidate, configurationStore);
            Assert.Equal(MqttPlatform.ThingsBoard, (await configurationStore.LoadAsync()).Mqtt.Platform);
            Assert.Empty(await store.ReadOutboxAsync(10));
        }
        finally { Directory.Delete(directory, true); }
    }
}
