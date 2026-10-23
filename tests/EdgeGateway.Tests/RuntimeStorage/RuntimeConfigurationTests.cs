using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task EditingAddingAndDeletingStoppedDevicesPreservesTheRunningPeerAndSharedServices()
    {
        await using var harness = new Harness();
        var first = harness.Config.Devices[0];
        var second = AddSecondDevice(harness);
        harness.Config.Mqtt.Enabled = true; harness.Config.PublishIntervalMs = 100;
        harness.Runtime.Configure(harness.Config);
        var configurationStore = new RecordingConfigurationStore(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count == 2 && harness.Publisher.Attempts > 0);
        await harness.Runtime.StopDeviceAsync(second.Id);
        var historyBefore = harness.Store.Samples.Count(sample => sample.DeviceId == first.Id);
        var publishesBefore = harness.Publisher.Attempts;

        var candidate = CopyConfiguration(harness.Config);
        var changed = candidate.Devices.Single(device => device.Id == second.Id);
        changed.Name = "已更新的停止设备"; changed.Tags[0].Name = "已更新点位"; changed.Tags[0].Scale = 3;
        var added = new DeviceConfiguration { DriverId = "test", Name = "新增设备", PollIntervalMs = 100,
            Tags = [new TagConfiguration { Name = "新增点位", Address = "2", DataType = TagDataType.Float64 }] };
        candidate.Devices.Add(added);
        var result = await harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore);
        Assert.Equal([added.Id], result.Added); Assert.Equal([second.Id], result.Updated);
        Assert.Empty(result.Restarted); Assert.Empty(result.Stopped);
        Assert.False(harness.Runtime.IsDeviceRunning(second.Id)); Assert.False(harness.Runtime.IsDeviceRunning(added.Id));
        Assert.DoesNotContain(harness.Runtime.GetLatest(), sample => sample.DeviceId == second.Id);
        var stopped = harness.Runtime.GetDeviceStatuses().Single(status => status.DeviceId == second.Id);
        Assert.Equal(changed.Name, stopped.DeviceName); Assert.Equal(DeviceState.Stopped, stopped.State);
        Assert.Null(stopped.LastSuccess);

        candidate.Devices.RemoveAll(device => device.Id == second.Id);
        result = await harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore);
        Assert.Equal([second.Id], result.Removed); Assert.Empty(result.Restarted);
        Assert.DoesNotContain(harness.Runtime.GetDeviceStatuses(), status => status.DeviceId == second.Id);
        await Until(() => harness.Store.Samples.Count(sample => sample.DeviceId == first.Id) > historyBefore
            && harness.Publisher.Attempts > publishesBefore);
        Assert.Equal(1, harness.Factory.CreationsByDevice[first.Id]);
        Assert.Equal(0, harness.Factory.DisposalsByDevice.GetValueOrDefault(first.Id));
        Assert.Equal(1, harness.Store.Initializations); Assert.Equal(0, harness.Publisher.Disconnections);
        Assert.Equal(ConfigurationJson(candidate), ConfigurationJson(await configurationStore.LoadAsync()));
        await harness.Runtime.StartDeviceAsync(added.Id);
        await Until(() => harness.Runtime.GetLatest().Any(sample => sample.DeviceId == added.Id && sample.TagName == "新增点位"));
    }

    [Fact]
    public async Task ApplyingRunningDeviceChangesCancelsOnlyItsOldWriteAndRestartsWithNewSettings()
    {
        await using var harness = new Harness();
        var first = harness.Config.Devices[0];
        var second = AddSecondDevice(harness); second.Tags[0].Writable = true;
        harness.Config.Mqtt.Enabled = true; harness.Config.PublishIntervalMs = 100;
        harness.Runtime.Configure(harness.Config);
        var configurationStore = new RecordingConfigurationStore(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count == 2);
        harness.Factory.BlockingWriteDevice = second.Id;
        var write = harness.Runtime.WriteAsync(second.Id, second.Tags[0].Id, "13");
        await harness.Factory.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var candidate = CopyConfiguration(harness.Config);
        var changed = candidate.Devices.Single(device => device.Id == second.Id);
        changed.Name = "局部重启设备"; changed.Tags[0].Scale = 2;
        var result = await harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore).WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal([second.Id], result.Updated); Assert.Equal([second.Id], result.Restarted); Assert.Empty(result.Stopped);
        await Until(() => harness.Runtime.GetLatest().Any(sample => sample.DeviceId == second.Id && sample.DeviceName == changed.Name && sample.NumericValue == 4));
        Assert.Equal(1, harness.Factory.CreationsByDevice[first.Id]);
        Assert.Equal(0, harness.Factory.DisposalsByDevice.GetValueOrDefault(first.Id));
        Assert.Equal(2, harness.Factory.CreationsByDevice[second.Id]);
        Assert.Equal(1, harness.Factory.DisposalsByDevice[second.Id]);
        Assert.Equal(1, harness.Factory.MaximumLiveDriversByDevice[second.Id]);
        Assert.Equal(1, harness.Store.Initializations); Assert.Equal(0, harness.Publisher.Disconnections);
        harness.Factory.BlockingWriteDevice = null;
        await harness.Runtime.WriteAsync(second.Id, second.Tags[0].Id, "12");
        Assert.Equal(6d, harness.Factory.LastWrite);
    }

    [Fact]
    public async Task DictionaryInsertionOrderDoesNotRestartAnUnchangedDevice()
    {
        await using var harness = new Harness();
        var device = harness.Config.Devices[0];
        device.Properties = new Dictionary<string, string> { ["alpha"] = "1", ["beta"] = "2" };
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count == 1);
        var candidate = CopyConfiguration(harness.Config);
        candidate.Devices[0].Properties = new Dictionary<string, string> { ["beta"] = "2", ["alpha"] = "1" };
        var result = await harness.Runtime.ApplyConfigurationAsync(candidate, new RecordingConfigurationStore(harness.Config));
        Assert.Empty(result.Added); Assert.Empty(result.Updated); Assert.Empty(result.Removed);
        Assert.Empty(result.Restarted); Assert.Empty(result.Stopped);
        Assert.Equal(1, harness.Factory.CreationsByDevice[device.Id]);
        Assert.Equal(0, harness.Factory.DisposalsByDevice.GetValueOrDefault(device.Id));
        Assert.Single(harness.Runtime.GetLatest());
    }

    [Fact]
    public async Task InvalidGlobalOrFailedSaveCandidatesLeaveStoredConfigurationAndSessionsUntouched()
    {
        await using var harness = new Harness();
        var device = harness.Config.Devices[0];
        var configurationStore = new RecordingConfigurationStore(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count == 1);
        var invalid = CopyConfiguration(harness.Config); invalid.Devices[0].PollIntervalMs = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Runtime.ApplyConfigurationAsync(invalid, configurationStore));
        var platform = CopyConfiguration(harness.Config); platform.Mqtt.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runtime.ApplyConfigurationAsync(platform, configurationStore));
        Assert.Equal(0, configurationStore.SaveAttempts);
        configurationStore.SaveFailure = new IOException("磁盘无法写入");
        var changed = CopyConfiguration(harness.Config); changed.Devices[0].Name = "不能应用的名称";
        await Assert.ThrowsAsync<IOException>(() => harness.Runtime.ApplyConfigurationAsync(changed, configurationStore));
        Assert.Equal(ConfigurationJson(harness.Config), ConfigurationJson(await configurationStore.LoadAsync()));
        Assert.True(harness.Runtime.IsDeviceRunning(device.Id));
        Assert.Equal(device.Name, Assert.Single(harness.Runtime.GetDeviceStatuses()).DeviceName);
        Assert.Equal(1, harness.Factory.CreationsByDevice[device.Id]); Assert.Equal(0, harness.Factory.Disposals);
        var reads = harness.Factory.ReadsByDevice[device.Id];
        await Until(() => harness.Factory.ReadsByDevice[device.Id] > reads);
    }

    [Fact]
    public async Task ApplyCancellationIsHonoredBeforePersistenceAndIgnoredAfterPersistenceCommits()
    {
        await using var harness = new Harness();
        var device = harness.Config.Devices[0];
        var configurationStore = new RecordingConfigurationStore(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count == 1);
        var candidate = CopyConfiguration(harness.Config); candidate.Devices[0].Tags[0].Scale = 3;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore, cancelled.Token));
        }
        Assert.Equal(0, configurationStore.SaveAttempts);
        using (var duringSave = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            configurationStore.BeforeSave = async token => { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); };
            var applying = harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore, duringSave.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                duringSave.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applying);
            }
            finally { duringSave.Cancel(); }
        }
        Assert.Equal(1, harness.Factory.CreationsByDevice[device.Id]); Assert.Equal(0, harness.Factory.Disposals);
        Assert.Equal(ConfigurationJson(harness.Config), ConfigurationJson(await configurationStore.LoadAsync()));
        using var afterSave = new CancellationTokenSource();
        configurationStore.BeforeSave = null; configurationStore.AfterSave = afterSave.Cancel;
        var applied = await harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore, afterSave.Token);
        Assert.True(afterSave.IsCancellationRequested); Assert.Equal([device.Id], applied.Restarted);
        await Until(() => harness.Runtime.GetLatest().Any(sample => sample.NumericValue == 6));
        Assert.Equal(2, harness.Factory.CreationsByDevice[device.Id]);
        Assert.Equal(ConfigurationJson(candidate), ConfigurationJson(await configurationStore.LoadAsync()));
    }

    [Fact]
    public async Task ConfigurationSaveSerializesWithLifecycleWhilePeersKeepCollecting()
    {
        await using var harness = new Harness();
        var first = harness.Config.Devices[0];
        var second = AddSecondDevice(harness);
        harness.Runtime.Configure(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count == 2);
        var candidate = CopyConfiguration(harness.Config); candidate.Devices[0].Name = "已保存的设备名称";
        var added = new DeviceConfiguration { DriverId = "test", Name = "事务后启动设备", PollIntervalMs = 100,
            Tags = [new TagConfiguration { Name = "事务后点位", Address = "2", DataType = TagDataType.Float64 }] };
        candidate.Devices.Add(added);
        var expected = CopyConfiguration(candidate);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var configurationStore = new RecordingConfigurationStore(harness.Config)
        { BeforeSave = async token => { entered.TrySetResult(true); await release.Task.WaitAsync(token); } };
        var applying = harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore);
        Task stopping = Task.CompletedTask, starting = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stopping = harness.Runtime.StopDeviceAsync(first.Id);
            starting = harness.Runtime.StartDeviceAsync(added.Id);
            Assert.False(stopping.IsCompleted); Assert.False(starting.IsCompleted);
            candidate.Devices[0].Name = "调用方随后篡改"; candidate.Mqtt.Host = "caller-change.invalid";
            var reads = harness.Factory.ReadsByDevice[first.Id];
            await Until(() => harness.Factory.ReadsByDevice[first.Id] > reads);
            Assert.Equal(0, harness.Factory.Disposals);
        }
        finally { release.TrySetResult(true); }
        await Task.WhenAll(applying, stopping, starting);
        var applied = await applying;
        Assert.Equal([first.Id], applied.Restarted); Assert.Equal([added.Id], applied.Added);
        Assert.False(harness.Runtime.IsDeviceRunning(first.Id));
        Assert.True(harness.Runtime.IsDeviceRunning(second.Id)); Assert.True(harness.Runtime.IsDeviceRunning(added.Id));
        await Until(() => harness.Runtime.GetLatest().Any(sample => sample.DeviceId == added.Id && sample.Quality == SampleQuality.Good));
        Assert.Equal(ConfigurationJson(expected), ConfigurationJson(await configurationStore.LoadAsync()));
        Assert.Equal(1, harness.Factory.CreationsByDevice[second.Id]);
        Assert.Equal(1, harness.Factory.MaximumLiveDriversByDevice[first.Id]);
        Assert.Equal(1, harness.Store.Initializations); Assert.Equal(0, harness.Publisher.Disconnections);
    }

    [Fact]
    public async Task SoleSessionCanRestartWithNoEnabledPointsAndOnlyFinalDisableStopsSharedServices()
    {
        await using var harness = new Harness();
        var device = harness.Config.Devices[0];
        harness.Config.Mqtt.Enabled = true; harness.Config.PublishIntervalMs = 100;
        harness.Runtime.Configure(harness.Config);
        var configurationStore = new RecordingConfigurationStore(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count == 1 && harness.Publisher.Attempts > 0);
        var candidate = CopyConfiguration(harness.Config); candidate.Devices[0].Tags[0].Enabled = false;
        var result = await harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore);
        Assert.Equal([device.Id], result.Restarted);
        await Until(() => Assert.Single(harness.Runtime.GetDeviceStatuses()).State == DeviceState.Running);
        Assert.Empty(harness.Runtime.GetLatest());
        Assert.Equal(1, harness.Store.Initializations); Assert.Equal(0, harness.Publisher.Disconnections);
        candidate.Devices[0].Enabled = false;
        result = await harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore);
        Assert.Equal([device.Id], result.Stopped); Assert.Empty(result.Restarted);
        Assert.False(harness.Runtime.IsRunning); Assert.Equal(1, harness.Publisher.Disconnections);
        Assert.NotEmpty(harness.Store.Samples);
        candidate.Devices[0].Enabled = true;
        result = await harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore);
        Assert.Empty(result.Restarted); Assert.False(harness.Runtime.IsRunning);
        await harness.Runtime.StartDeviceAsync(device.Id);
        await Until(() => Assert.Single(harness.Runtime.GetDeviceStatuses()).State == DeviceState.Running);
        Assert.Equal(3, harness.Factory.CreationsByDevice[device.Id]); Assert.Equal(2, harness.Store.Initializations);
        await harness.Runtime.StopAsync();
        Assert.Equal(0, harness.Factory.LiveDriversByDevice[device.Id]); Assert.Equal(2, harness.Publisher.Disconnections);
    }

    [Fact]
    public async Task FailedConnectionAfterApplyIsVisibleAndCanBeCorrectedWithoutRestartingThePeer()
    {
        await using var harness = new Harness();
        var first = harness.Config.Devices[0];
        var second = AddSecondDevice(harness);
        harness.Runtime.Configure(harness.Config);
        var configurationStore = new RecordingConfigurationStore(harness.Config);
        await harness.Runtime.StartAsync();
        await Until(() => harness.Runtime.GetLatest().Count == 2);
        harness.Factory.FailingHosts["offline.invalid"] = true;
        var candidate = CopyConfiguration(harness.Config);
        candidate.Devices.Single(device => device.Id == second.Id).Host = "offline.invalid";
        var result = await harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore);
        Assert.Equal([second.Id], result.Restarted);
        await Until(() => harness.Runtime.GetDeviceStatuses().Any(status => status.DeviceId == second.Id
            && status.State == DeviceState.Reconnecting && status.Error!.Contains("offline.invalid")));
        Assert.Equal("offline.invalid", (await configurationStore.LoadAsync()).Devices.Single(device => device.Id == second.Id).Host);
        Assert.True(harness.Runtime.IsDeviceRunning(first.Id)); Assert.True(harness.Runtime.IsDeviceRunning(second.Id));
        var peerReads = harness.Factory.ReadsByDevice[first.Id];
        await Until(() => harness.Factory.ReadsByDevice[first.Id] > peerReads);
        candidate.Devices.Single(device => device.Id == second.Id).Host = "recovered.invalid";
        await harness.Runtime.ApplyConfigurationAsync(candidate, configurationStore);
        await Until(() => harness.Runtime.GetLatest().Any(sample => sample.DeviceId == second.Id && sample.Quality == SampleQuality.Good));
        Assert.Equal(1, harness.Factory.CreationsByDevice[first.Id]);
        Assert.Equal(0, harness.Factory.DisposalsByDevice.GetValueOrDefault(first.Id));
        Assert.Equal(1, harness.Factory.MaximumLiveDriversByDevice[second.Id]);
        Assert.Equal(1, harness.Store.Initializations); Assert.Equal(0, harness.Publisher.Disconnections);
    }

    private static GatewayConfiguration CopyConfiguration(GatewayConfiguration configuration) =>
        JsonSerializer.Deserialize<GatewayConfiguration>(ConfigurationJson(configuration))!;
    private static string ConfigurationJson(GatewayConfiguration configuration) => JsonSerializer.Serialize(configuration);

    private sealed class RecordingConfigurationStore(GatewayConfiguration initial) : IConfigurationStore
    {
        private GatewayConfiguration _saved = CopyConfiguration(initial);
        public int SaveAttempts;
        public Exception? SaveFailure;
        public Func<CancellationToken, Task>? BeforeSave;
        public Action? AfterSave;
        public Task<GatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(CopyConfiguration(_saved));
        public async Task SaveAsync(GatewayConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SaveAttempts);
            var snapshot = CopyConfiguration(configuration);
            if (BeforeSave is not null) await BeforeSave(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveFailure is not null) throw SaveFailure;
            _saved = snapshot;
            AfterSave?.Invoke();
        }
    }
}
