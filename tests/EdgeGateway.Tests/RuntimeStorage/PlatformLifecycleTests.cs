using EdgeGateway.Model;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task PlatformHotApplyAndManualStopPreserveDeviceAndOutputWorkers()
    {
        await using var h = new Harness();
        h.Config.Mqtt.Enabled = true; h.Config.PublishIntervalMs = 100;
        h.Runtime.Configure(h.Config);
        var store = new RecordingConfigurationStore(h.Config);
        var device = h.Config.Devices[0];
        await h.Runtime.StartAsync();
        await Until(() => h.Publisher.Attempts > 0);
        var next = CopyConfiguration(h.Config); next.Mqtt.ClientId = "hot-client";
        var result = await h.Runtime.ApplyConfigurationAsync(next, store);
        Assert.Empty(result.Restarted); Assert.Empty(result.Stopped);
        Assert.Equal(1, h.Factory.CreationsByDevice[device.Id]);
        await Until(() => h.Publisher.ClientIds.Contains("hot-client"));
        Assert.True(h.Runtime.GetTelemetryStatus().Running);

        await h.Runtime.StopPlatformAsync();
        var attempts = h.Publisher.Attempts;
        var rows = h.Store.Samples.Count;
        await Until(() => h.Store.Samples.Count > rows + 2);
        Assert.Equal(attempts, h.Publisher.Attempts);
        Assert.False(h.Runtime.GetTelemetryStatus().Running);
        next.Mqtt.ClientId = "saved-while-stopped";
        await h.Runtime.ApplyConfigurationAsync(next, store);
        Assert.False(h.Runtime.GetTelemetryStatus().Running);
        await h.Runtime.StartPlatformAsync();
        await Until(() => h.Publisher.ClientIds.Contains("saved-while-stopped"));
        Assert.Equal(1, h.Factory.CreationsByDevice[device.Id]);
        Assert.Equal(0, h.Factory.Disposals);
        // Explicitly started platform remains usable after the last device stops.
        await h.Runtime.StopDeviceAsync(device.Id);
        Assert.False(h.Runtime.IsRunning);
        Assert.True(h.Runtime.GetTelemetryStatus().Running);
        await h.Runtime.StopAsync();
        Assert.False(h.Runtime.GetTelemetryStatus().Running);
    }

    [Fact]
    public async Task PlatformFailedSaveAndCancellationResumeOriginalSettingsWithoutRestartingDevice()
    {
        await using var h = new Harness();
        h.Config.Mqtt.Enabled = true; h.Config.PublishIntervalMs = 100;
        h.Runtime.Configure(h.Config);
        await h.Runtime.StartAsync();
        await Until(() => h.Publisher.Attempts > 0);
        var store = new RecordingConfigurationStore(h.Config) { SaveFailure = new IOException("save failure") };
        var next = CopyConfiguration(h.Config); next.Mqtt.ClientId = "never-committed";
        await Assert.ThrowsAsync<IOException>(() => h.Runtime.ApplyConfigurationAsync(next, store));
        Assert.True(h.Runtime.GetTelemetryStatus().Running);
        Assert.Equal(h.Config.Mqtt.ClientId, (await store.LoadAsync()).Mqtt.ClientId);
        Assert.DoesNotContain("never-committed", h.Publisher.ClientIds);
        store.SaveFailure = null;
        using var cancellation = new CancellationTokenSource();
        store.BeforeSave = token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Runtime.ApplyConfigurationAsync(next, store, cancellation.Token));
        Assert.True(h.Runtime.GetTelemetryStatus().Running);
        Assert.Equal(1, h.Factory.CreationsByDevice[h.Config.Devices[0].Id]);
        Assert.DoesNotContain("never-committed", h.Publisher.ClientIds);
    }

    [Fact]
    public async Task PlatformEnableDisableDuringCollectionDoesNotStopAcquisition()
    {
        await using var h = new Harness();
        var store = new RecordingConfigurationStore(h.Config);
        await h.Runtime.StartAsync();
        var next = CopyConfiguration(h.Config); next.Mqtt.Enabled = true; next.PublishIntervalMs = 100;
        await h.Runtime.ApplyConfigurationAsync(next, store);
        await Until(() => h.Publisher.Attempts > 0);
        next.Mqtt.Enabled = false;
        await h.Runtime.ApplyConfigurationAsync(next, store);
        Assert.False(h.Runtime.GetTelemetryStatus().Enabled);
        Assert.False(h.Runtime.GetTelemetryStatus().Running);
        var rows = h.Store.Samples.Count;
        await Until(() => h.Store.Samples.Count > rows);
        Assert.Equal(1, h.Factory.CreationsByDevice[h.Config.Devices[0].Id]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runtime.StartPlatformAsync());
    }

    [Fact]
    public async Task PlatformSwitchChecksQueueAfterQuiescingAndRetainsOldPayloadOnFailure()
    {
        await using var h = new Harness();
        h.Config.Mqtt.Enabled = true; h.Config.PublishIntervalMs = 100;
        h.Publisher.Fail = true;
        h.Runtime.Configure(h.Config);
        await h.Runtime.StartAsync();
        await Until(() => h.Store.Outbox.Count > 0);
        var store = new RecordingConfigurationStore(h.Config);
        var next = CopyConfiguration(h.Config); next.Mqtt.Platform = MqttPlatform.ThingsBoard;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runtime.ApplyConfigurationAsync(next, store));
        Assert.Equal(0, store.SaveAttempts);
        Assert.NotEmpty(h.Store.Outbox);
        Assert.True(h.Runtime.GetTelemetryStatus().Running);
        next.Mqtt.Platform = MqttPlatform.Generic; next.Mqtt.ClientId = "confirm-after-stop";
        await h.Runtime.ApplyConfigurationAsync(next, store, beforeSave: async token =>
        {
            Assert.False(h.Runtime.GetTelemetryStatus().Running);
            var pending = h.Store.Outbox.Count;
            var attempts = h.Publisher.Attempts;
            var reads = h.Factory.ReadsByDevice[h.Config.Devices[0].Id];
            await Until(() => h.Factory.ReadsByDevice[h.Config.Devices[0].Id] > reads + 1);
            Assert.Equal(pending, h.Store.Outbox.Count);
            Assert.Equal(attempts, h.Publisher.Attempts);
        });
        Assert.True(h.Runtime.GetTelemetryStatus().Running);
        Assert.Equal(1, h.Factory.CreationsByDevice[h.Config.Devices[0].Id]);
    }

    [Fact]
    public async Task PlatformCanStartWithoutDevicesAndStopDoesNotDiscardBacklog()
    {
        await using var h = new Harness();
        h.Config.Mqtt.Enabled = true; h.Runtime.Configure(h.Config);
        await h.Store.EnqueueAsync("old-topic", "old-payload", 100);
        h.Publisher.Fail = true;
        await h.Runtime.StartPlatformAsync();
        await Until(() => h.Publisher.Attempts > 0);
        await h.Runtime.StopPlatformAsync();
        Assert.Single(h.Store.Outbox);
        Assert.False(h.Runtime.IsRunning);
        Assert.Empty(h.Factory.CreationsByDevice);
        h.Publisher.Fail = false;
        await h.Runtime.StartPlatformAsync();
        await Until(() => h.Store.Outbox.IsEmpty);
        Assert.Contains("old-payload", h.Publisher.Payloads);
        await h.Runtime.StopAsync();
        Assert.False(h.Runtime.GetTelemetryStatus().Running);
    }
}
