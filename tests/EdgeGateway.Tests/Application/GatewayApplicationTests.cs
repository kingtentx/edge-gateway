using System.Collections.Concurrent;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Application;
using EdgeGateway.DataAccess;
using EdgeGateway.Driver.Simulator;
using EdgeGateway.Hosting;
using EdgeGateway.Model;
using Core = EdgeGateway.Service;

namespace EdgeGateway.Tests.Application;

public sealed class GatewayApplicationTests
{
    [Fact]
    public async Task DriverListIncludesBundledDriversWithoutUploadedPlugins()
    {
        await using var h = await Harness.CreateAsync();
        h.Configurations.Inventory = [
            new("simulator", "旧名称", "fixture", true, false, "Simulator", "1.2.3.0", "fixture-hash", "bundled"),
            new("stale", "过期记录", "fixture", false, false, "Stale", "1.0", "", "bundled")
        ];
        Assert.Empty(h.App.GetInstalledDriverPlugins());
        var driver = Assert.Single(await h.App.GetDriverListAsync());
        Assert.Equal("simulator", driver.Id);
        Assert.Equal(h.App.Drivers.GetDescriptor("simulator").Name, driver.Name);
        Assert.Equal("1.2.3.0", driver.Version);
        Assert.Equal("Simulator", driver.AssemblyName);
        Assert.Equal("fixture-hash", driver.Sha256);
        Assert.Equal("bundled", driver.Source);
        Assert.Equal("已加载", driver.Status);
        Assert.True(driver.IsAvailable);
    }

    [Fact]
    public async Task DriverListMergesLiveUploadsAndDoesNotTreatSavedPluginsAsLoaded()
    {
        DriverListRegistry registry = null!;
        await using var h = await Harness.CreateAsync(wrapRegistry: inner => registry = new(inner));
        Assert.Single(await h.App.GetDriverListAsync());
        registry.AdditionalDrivers.Add(new("custom", "新上传驱动", "fixture", false));
        registry.Plugins.Add(new("custom", "新上传驱动", "2.0", "Custom", "uploaded-hash", "已加载"));
        registry.Plugins.Add(new("missing", "文件丢失", "1.0", "Missing", "old-hash", "等待文件校验"));
        var drivers = await h.App.GetDriverListAsync();
        Assert.Equal(3, drivers.Count);
        var uploaded = Assert.Single(drivers, driver => driver.Id == "custom");
        Assert.True(uploaded.IsAvailable);
        Assert.Equal("uploaded", uploaded.Source);
        Assert.Equal("2.0", uploaded.Version);
        Assert.Equal("uploaded-hash", uploaded.Sha256);
        var missing = Assert.Single(drivers, driver => driver.Id == "missing");
        Assert.False(missing.IsAvailable);
        Assert.Contains("未加载", missing.Status);
        Assert.Equal("uploaded", missing.Source);
        Assert.Equal("unknown", drivers.Single(driver => driver.Id == "simulator").Version);
    }

    [Fact]
    public async Task FirstStartPersistsDefaultIdentitiesBeforeCreatingAnyDriver()
    {
        await using var h = await Harness.CreateAsync(persisted: false);
        var original = h.App.GetConfiguration();
        await h.App.StartDeviceAsync(original.Devices[0].Id);
        await Until(() => h.App.GetLatest().Count > 0);
        Assert.Equal(1, h.Configurations.Saves);
        Assert.All(h.Factory.CreatedAfterSave, saved => Assert.True(saved));
        var saved = await h.App.ReadSavedConfigurationAsync();
        Assert.Equal(original.Devices[0].Id, saved.Devices[0].Id);
        Assert.Equal(original.Devices[0].Tags[0].Id, saved.Devices[0].Tags[0].Id);
        await h.App.StartDeviceAsync(original.Devices[1].Id);
        Assert.Equal(1, h.Configurations.Saves);
        Assert.True(h.App.IsDeviceRunning(original.Devices[0].Id));
        Assert.True(h.App.IsDeviceRunning(original.Devices[1].Id));
    }

    [Fact]
    public async Task FailedInitialSaveDoesNotOpenDeviceOrPublishDraft()
    {
        await using var h = await Harness.CreateAsync(persisted: false);
        var before = Json(h.App.GetConfiguration());
        h.Configurations.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => h.App.StartDeviceAsync(h.A.Id));
        Assert.False(h.App.IsRunning);
        Assert.Empty(h.Factory.CreatedAfterSave);
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        h.Configurations.FailSave = false;
        await h.App.StartDeviceAsync(h.A.Id);
        await Until(() => h.App.GetLatest().Count > 0);
        Assert.Equal(1, h.Configurations.Saves);
    }

    [Fact]
    public async Task FailedEditLeavesRunningConnectionAndAppliedSnapshotIntact()
    {
        await using var h = await Harness.CreateAsync();
        await h.App.StartDeviceAsync(h.A.Id);
        await Until(() => h.App.GetLatest().Count > 0);
        var before = Json(h.App.GetConfiguration());
        var previousSample = h.App.GetLatest().Single().Timestamp;
        var edited = h.App.GetConfiguration().Devices[0];
        edited.Name = "保存失败的名称";
        h.Configurations.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => h.App.UpdateDeviceAsync(edited));
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(before, Json(await h.App.ReadSavedConfigurationAsync()));
        Assert.Equal(1, h.Factory.Created[h.A.Id]);
        Assert.False(h.Factory.Disposed.ContainsKey(h.A.Id));
        await Until(() => h.App.GetLatest().Any(sample => sample.DeviceId == h.A.Id && sample.Timestamp > previousSample));
    }

    [Fact]
    public async Task ConcurrentDeviceCommandsRetainBothUpdatesFromLatestConfiguration()
    {
        await using var h = await Harness.CreateAsync();
        var first = h.App.GetConfiguration().Devices[0];
        var second = h.App.GetConfiguration().Devices[1];
        first.Name = "第一台已保存";
        second.Name = "第二台已保存";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Configurations.BlockNextSave = release.Task;
        var firstTask = h.App.UpdateDeviceAsync(first);
        await h.Configurations.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = h.App.UpdateDeviceAsync(second);
        first.Name = "调用方后来修改";
        second.Tags[0].Name = "不应进入运行配置";
        release.SetResult();
        await Task.WhenAll(firstTask, secondTask);
        var saved = await h.App.ReadSavedConfigurationAsync();
        Assert.Equal("第一台已保存", saved.Devices[0].Name);
        Assert.Equal("第二台已保存", saved.Devices[1].Name);
        Assert.Equal("数值", saved.Devices[1].Tags[0].Name);
        Assert.Equal(Json(saved), Json(h.App.GetConfiguration()));
        Assert.Equal(2, h.Configurations.Saves);
    }

    [Fact]
    public async Task DeviceAndPointCommandsOnlyRestartTheirRunningTarget()
    {
        await using var h = await Harness.CreateAsync();
        await h.App.StartDeviceAsync(h.A.Id);
        await Until(() => h.App.GetLatest().Count > 0);
        var added = Device("新设备");
        var addition = await h.App.AddDeviceAsync(added);
        Assert.Contains(added.Id, addition.Added);
        Assert.False(h.App.IsDeviceRunning(added.Id));
        Assert.Equal(1, h.Factory.Created[h.A.Id]);
        await h.App.StartDeviceAsync(h.B.Id);
        await Until(() => h.App.GetLatest().Any(sample => sample.DeviceId == h.B.Id));
        var tag = new TagConfiguration { Name = "新增点位", Address = "constant:7", DataType = TagDataType.Float64 };
        var changed = await h.App.AddTagAsync(h.B.Id, tag);
        Assert.Equal(new[] { h.B.Id }, changed.Restarted);
        await Until(() => h.App.GetLatest().Any(sample => sample.TagId == tag.Id));
        Assert.Equal(1, h.Factory.Created[h.A.Id]);
        Assert.Equal(2, h.Factory.Created[h.B.Id]);
        var lastA = h.App.GetLatest().First(sample => sample.DeviceId == h.A.Id).Timestamp;
        await h.App.DeleteDeviceAsync(h.B.Id);
        Assert.False(h.App.IsDeviceRunning(h.B.Id));
        Assert.DoesNotContain(h.App.GetLatest(), sample => sample.DeviceId == h.B.Id);
        await Until(() => h.App.GetLatest().Any(sample => sample.DeviceId == h.A.Id && sample.Timestamp > lastA));
        Assert.Equal(1, h.Factory.Created[h.A.Id]);
    }

    [Fact]
    public async Task GlobalDraftKeepsLaterDeviceAndChannelChangesAndCannotApplyWhileRunning()
    {
        await using var h = await Harness.CreateAsync();
        var oldDraft = h.App.GetConfiguration();
        oldDraft.Name = "新网关名称";
        oldDraft.Mqtt.ClientId = "new-client";
        oldDraft.Devices.Clear();
        oldDraft.Channels.Clear();
        var channel = new ChannelConfiguration { Name = "后来添加的通道", InterRequestDelayMs = 15 };
        await h.App.UpsertChannelAsync(channel, isNew: true);
        var added = Device("后来添加的设备");
        added.ChannelId = channel.Id;
        await h.App.AppendDevicesAsync([added]);
        await h.App.ApplyGlobalSettingsAsync(oldDraft);
        var applied = h.App.GetConfiguration();
        Assert.Equal("新网关名称", applied.Name);
        Assert.Equal("new-client", applied.Mqtt.ClientId);
        Assert.Equal(3, applied.Devices.Count);
        Assert.Equal(channel.Id, Assert.Single(applied.Channels).Id);
        Assert.Contains(applied.Devices, device => device.Id == added.Id && device.ChannelId == channel.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.DeleteChannelAsync(channel.Id));
        await h.App.StartDeviceAsync(h.A.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.ApplyGlobalSettingsAsync(oldDraft));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.UpsertChannelAsync(new() { Name = "运行期间通道" }, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.ReplaceConfigurationAsync(applied));
        Assert.True(h.App.IsDeviceRunning(h.A.Id));
    }

    [Fact]
    public async Task InvalidPointBatchCannotPartiallyModifyConfiguration()
    {
        await using var h = await Harness.CreateAsync();
        var before = Json(h.App.GetConfiguration());
        var valid = new TagConfiguration { Name = "有效", Address = "constant:4", DataType = TagDataType.Float64 };
        var invalid = new TagConfiguration { Name = "无效", Address = "invalid-address", DataType = TagDataType.Float64 };
        await Assert.ThrowsAsync<InvalidDataException>(() => h.App.ReplaceDeviceTagsAsync(h.A.Id, [valid, invalid]));
        Assert.Equal(0, h.Configurations.Saves);
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(before, Json(await h.App.ReadSavedConfigurationAsync()));
    }

    [Fact]
    public async Task CancellationBeforeSavePreservesStateButCancellationAfterCommitPublishesAppliedState()
    {
        await using var h = await Harness.CreateAsync();
        var before = Json(h.App.GetConfiguration());
        var edited = h.App.GetConfiguration().Devices[0];
        edited.Name = "应保存的名称";
        using var beforeSave = new CancellationTokenSource();
        h.Configurations.BlockNextSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var cancelled = h.App.UpdateDeviceAsync(edited, beforeSave.Token);
        await h.Configurations.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        beforeSave.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(0, h.Configurations.Saves);

        using var afterSave = new CancellationTokenSource();
        h.Configurations.AfterSave = afterSave.Cancel;
        var committed = await h.App.UpdateDeviceAsync(edited, afterSave.Token);
        Assert.True(afterSave.IsCancellationRequested);
        Assert.Contains(edited.Id, committed.Updated);
        Assert.Equal(edited.Name, h.App.GetConfiguration().Devices[0].Name);
        Assert.Equal(edited.Name, (await h.App.ReadSavedConfigurationAsync()).Devices[0].Name);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("configuration")]
    [InlineData("queue")]
    public async Task DestinationChangeRequiresCurrentBoundConfirmation(string changedSincePreview)
    {
        await using var h = await Harness.CreateAsync();
        Assert.True(await h.Store.EnqueueAsync("old/topic", "{\"messageId\":\"old\"}", 100));
        var candidate = h.App.GetConfiguration();
        candidate.Mqtt.Host = "new-broker.example.test";
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.ApplyConfigurationAsync(candidate));
        var preview = await h.App.PreviewConfigurationAsync(candidate);
        Assert.True(preview.RequiresMqttDestinationConfirmation);
        Assert.Equal(1, preview.PendingOutboxMessages);
        Assert.NotNull(preview.ConfirmationToken);
        if (changedSincePreview == "candidate") candidate.Name = "确认后改变候选";
        else if (changedSincePreview == "configuration") await h.App.AddDeviceAsync(Device("期间添加的设备"));
        else Assert.True(await h.Store.EnqueueAsync("old/topic", "{\"messageId\":\"later\"}", 100));
        var before = Json(h.App.GetConfiguration());
        var savesBefore = h.Configurations.Saves;
        var confirmation = new ConfigurationApplyOptions { MqttDestinationConfirmation = preview.ConfirmationToken };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.ApplyConfigurationAsync(candidate, confirmation));
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(savesBefore, h.Configurations.Saves);

        candidate = h.App.GetConfiguration();
        candidate.Mqtt.Host = "new-broker.example.test";
        preview = await h.App.PreviewConfigurationAsync(candidate);
        await h.App.ApplyConfigurationAsync(candidate, new() { MqttDestinationConfirmation = preview.ConfirmationToken });
        Assert.Equal("new-broker.example.test", h.App.GetConfiguration().Mqtt.Host);
        Assert.Equal("new-broker.example.test", (await h.App.ReadSavedConfigurationAsync()).Mqtt.Host);
        var retained = await h.Store.ReadOutboxAsync(10);
        Assert.Contains(retained, message => message.Payload == "{\"messageId\":\"old\"}" && message.Topic == "old/topic");
    }

    [Fact]
    public async Task ApplicationDiagnosticsReadbackAndFileExchangeUseTheSameAppliedStateWithoutLocalHistory()
    {
        await using var h = await Harness.CreateAsync();
        var detached = h.App.GetConfiguration();
        detached.Devices[0].Tags.Clear();
        detached.Mqtt.Host = "unapplied";
        Assert.Single(h.App.GetConfiguration().Devices[0].Tags);
        Assert.Equal("localhost", h.App.GetConfiguration().Mqtt.Host);
        var probe = await h.App.ProbeDeviceAsync(h.A.Id, h.A.Tags[0].Id);
        Assert.True(probe.Success);
        Assert.False(probe.UsedRunningSession);
        Assert.Empty(h.App.GetLatest());
        await h.App.StartDeviceAsync(h.A.Id);
        await Until(() => h.App.GetLatest().Count > 0);
        var write = await h.App.WriteVerifiedAsync(h.A.Id, h.A.Tags[0].Id, "42.5");
        Assert.Equal(WriteCommandState.Matched, write.State);
        Assert.True(write.Submitted);
        Assert.Equal(42.5, h.App.GetLatestSample(h.A.Tags[0].Id)!.NumericValue);
        await h.App.StopAsync();
        Assert.Empty(h.App.GetDatabaseStatuses());
        Assert.False(File.Exists(Path.Combine(h.Directory, "gateway.db")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.QueryExternalHistoryAsync(Guid.NewGuid(),
            new(h.A.Id, null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1))));
        Assert.Contains(h.App.GetCommunicationStatistics(), statistics => statistics.DeviceId == h.A.Id);
        var path = Path.Combine(h.Directory, "export.json");
        await h.App.ExportConfigurationAsync(path);
        Assert.Equal(Json(h.App.GetConfiguration()), Json(await h.App.ReadConfigurationFileAsync(path)));
    }

    [Fact]
    public async Task HostOwnsDirectoryLeaseAndPersistedIdentityAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-host-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Loaded protocol DLLs remain mapped on Windows. Stage them with the
            // test binaries, while each host's writable runtime stays isolated.
            var drivers = Path.Combine(AppContext.BaseDirectory, "drivers");
            Directory.CreateDirectory(drivers);
            foreach (var assembly in new[] { typeof(SimulatorDriverFactory).Assembly,
                         typeof(EdgeGateway.Driver.Modbus.ModbusTcpDriverFactory).Assembly })
            {
                var destination = Path.Combine(drivers, Path.GetFileName(assembly.Location));
                if (!File.Exists(destination)) File.Copy(assembly.Location, destination);
            }
            var host = await GatewayHost.CreateAsync(directory, pluginDirectory: drivers, runtimeDirectory: directory);
            var original = host.Application.GetConfiguration();
            try
            {
                Assert.False(host.Application.IsRunning);
                Assert.False(File.Exists(Path.Combine(directory, "gateway.json")));
                Assert.Contains(host.Application.Drivers.Drivers, descriptor => descriptor.Id == "OPC.UaClient");
                var driverList = await host.Application.GetDriverListAsync();
                Assert.Equal(host.Application.Drivers.Drivers.Select(driver => driver.Id).Order(), driverList.Where(driver => driver.IsAvailable).Select(driver => driver.Id).Order());
                Assert.Equal(15, driverList.Count(driver => driver.IsAvailable));
                Assert.All(driverList, driver => Assert.Equal("bundled", driver.Source));
                Assert.Equal(64, driverList.Single(driver => driver.Id == "simulator").Sha256.Length);
                await Assert.ThrowsAsync<InvalidOperationException>(() => GatewayHost.CreateAsync(directory,
                    pluginDirectory: drivers, runtimeDirectory: directory));
                var simulator = original.Devices.Single(device => device.DriverId == "simulator");
                simulator.Name = "驱动清单刷新后保存的设备";
                await host.Application.UpdateDeviceAsync(simulator);
                Assert.Equal(simulator.Name, (await host.Application.ReadSavedConfigurationAsync()).Devices.Single(device => device.Id == simulator.Id).Name);
                await host.Application.StartDeviceAsync(simulator.Id);
                await Until(() => host.Application.GetLatest().Count > 0);
                Assert.True(File.Exists(Path.Combine(directory, "gateway.config.db")));
                Assert.False(File.Exists(Path.Combine(directory, "gateway.json")));
                Assert.False(File.Exists(Path.Combine(directory, "gateway.db")));
            }
            finally { await host.DisposeAsync(); }
            await using var reopened = await GatewayHost.CreateAsync(directory, pluginDirectory: drivers, runtimeDirectory: directory);
            Assert.False(reopened.Application.IsRunning);
            Assert.Equal(original.Devices.Select(device => device.Id), reopened.Application.GetConfiguration().Devices.Select(device => device.Id));
            Assert.Equal("驱动清单刷新后保存的设备", reopened.Application.GetConfiguration().Devices.Single(device => device.DriverId == "simulator").Name);
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedHostLoadReleasesDirectoryLeaseWithoutReplacingCorruptConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-host-failure-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "gateway.config.db");
            const string invalid = "{not-valid-json";
            await File.WriteAllTextAsync(path, invalid);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => GatewayHost.CreateAsync(directory, runtimeDirectory: directory));
            Assert.Equal(invalid, await File.ReadAllTextAsync(path));
            using var lease = new FileStream(Path.Combine(directory, "gateway.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, GatewayJson.Options);

    [Fact]
    public async Task LoggingChangesApplyLiveWithoutRestartAndFailedSavePreservesActiveSettings()
    {
        await using var h = await Harness.CreateAsync();
        await h.App.StartAsync();
        await Until(() => h.App.GetLatest().Count == 2);
        var creations = h.Factory.Created.ToDictionary(pair => pair.Key, pair => pair.Value);
        var settings = new GatewayLoggingConfiguration { MinimumLevel = GatewayLogLevel.Error, MaxFileSizeMb = 5, RetainedFileCount = 8 };
        var result = await h.App.ApplyLoggingSettingsAsync(settings);
        Assert.Empty(result.Restarted);
        Assert.Equal(GatewayLogLevel.Error, (await h.App.ReadSavedConfigurationAsync()).Logging.MinimumLevel);
        Assert.All(creations, pair => Assert.Equal(pair.Value, h.Factory.Created[pair.Key]));
        settings.MaxFileSizeMb = 100;
        Assert.Equal(5, h.App.GetConfiguration().Logging.MaxFileSizeMb);
        h.Configurations.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => h.App.ApplyLoggingSettingsAsync(new() { MinimumLevel = GatewayLogLevel.Debug }));
        Assert.Equal(GatewayLogLevel.Error, h.App.GetConfiguration().Logging.MinimumLevel);
        var marker = "debug-after-rejected-config-" + Guid.NewGuid();
        h.App.Log.Write(GatewayLogLevel.Debug, marker);
        Assert.DoesNotContain(h.App.Log.GetEntries(), entry => entry.Message == marker);
    }

    [Fact]
    public async Task DatabaseSettingsRequireStoppedDevicesAndMergeIntoLatestConfiguration()
    {
        await using var h = await Harness.CreateAsync();
        var connection = new DatabaseConnectionConfiguration { Name = "reporting", Enabled = false };
        var group = new DatabaseWriteGroupConfiguration { Name = "points", ConnectionId = connection.Id };
        await h.App.StartDeviceAsync(h.A.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.ApplyDatabaseSettingsAsync([connection], [group]));
        Assert.Empty(h.App.GetConfiguration().Databases);
        await h.App.StopAsync();
        var added = Device("added after opening editor");
        await h.App.AddDeviceAsync(added);
        await h.App.ApplyDatabaseSettingsAsync([connection], [group]);
        Assert.Contains(h.App.GetConfiguration().Devices, device => device.Id == added.Id);
        Assert.Equal(connection.Id, Assert.Single(h.App.GetConfiguration().Databases).Id);
        Assert.Equal(group.Id, Assert.Single((await h.App.ReadSavedConfigurationAsync()).DatabaseWriteGroups).Id);
    }
    [Fact]
    public async Task DatabaseMappingSaveRejectsDuplicateNamesAndAllowsEditingTheSameGroup()
    {
        await using var h = await Harness.CreateAsync();
        var connection = new DatabaseConnectionConfiguration { Name = "mapping save fixture", Enabled = false };
        var original = new DatabaseWriteGroupConfiguration { ConnectionId = connection.Id };
        await h.App.ApplyDatabaseSettingsAsync([connection], [original]);
        var mapping = new DatabaseWriteGroupConfiguration
        {
            ConnectionId = connection.Id, Name = "  " + original.Name + "  ", Table = "custom_samples",
            Mode = DatabaseStorageMode.PointMapping, TagIds = [h.A.Tags[0].Id],
            Columns = [new() { Column = "_NAME", Source = DatabaseValueSource.TagName },
                new() { Column = "_QUALITY", Source = DatabaseValueSource.Constant, FieldType = DatabaseFieldType.Int64, ConstantValue = "7" }]
        };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => h.App.ApplyDatabaseSettingsAsync([connection], [original, mapping]));
        Assert.Contains("名称已存在", error.Message);
        Assert.DoesNotContain("1–100", error.Message);
        Assert.Equal(original.Id, Assert.Single(h.App.GetConfiguration().DatabaseWriteGroups).Id);
        Assert.Equal(original.Id, Assert.Single((await h.App.ReadSavedConfigurationAsync()).DatabaseWriteGroups).Id);
        mapping.Name = "采集数据（2）";
        await h.App.ApplyDatabaseSettingsAsync([connection], [original, mapping]);
        mapping.Columns[1].ConstantValue = "9";
        await h.App.ApplyDatabaseSettingsAsync([connection], [original, mapping]);
        var saved = (await h.App.ReadSavedConfigurationAsync()).DatabaseWriteGroups;
        Assert.Equal(2, saved.Count);
        Assert.Equal(original.Id, saved[0].Id);
        Assert.Equal(mapping.Id, saved[1].Id);
        Assert.Equal("9", saved[1].Columns[1].ConstantValue);
    }

    private static DeviceConfiguration Device(string name) => new()
    {
        Name = name, DriverId = "simulator", PollIntervalMs = 100, TimeoutMs = 1000,
        Tags = [new() { Name = "数值", Address = "constant:2", DataType = TagDataType.Float64, Writable = true }]
    };
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "edge-app-test-" + Guid.NewGuid().ToString("N"));
        public MemoryConfigurationStore Configurations { get; private set; } = null!;
        public FileGatewayStore Store { get; private set; } = null!;
        public CountingFactory Factory { get; private set; } = null!;
        public GatewayApplication App { get; private set; } = null!;
        public DeviceConfiguration A { get; private set; } = null!;
        public DeviceConfiguration B { get; private set; } = null!;
        private readonly NoNetworkPublisher _publisher = new();
        public static async Task<Harness> CreateAsync(bool persisted = true, Func<IDriverRegistry, IDriverRegistry>? wrapRegistry = null)
        {
            var h = new Harness();
            System.IO.Directory.CreateDirectory(h.Directory);
            h.Store = new FileGatewayStore(Path.Combine(h.Directory, "messaging"));
            await h.Store.InitializeAsync();
            h.A = Device("设备 A"); h.B = Device("设备 B");
            var configuration = new GatewayConfiguration { Devices = [h.A, h.B] };
            h.Configurations = new(configuration);
            h.Factory = new(h.Configurations);
            var log = new Core.GatewayLog();
            IDriverRegistry registry = new Core.DriverRegistry([h.Factory], log);
            h.App = new(configuration, wrapRegistry?.Invoke(registry) ?? registry, h.Configurations,
                h.Store, h._publisher, log, new JsonConfigurationFiles(), persisted);
            return h;
        }
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await _publisher.DisposeAsync();
            await Store.DisposeAsync();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class DriverListRegistry(IDriverRegistry inner) : IDriverRegistry, IDriverPluginManager
    {
        public List<DriverDescriptor> AdditionalDrivers { get; } = [];
        public List<DriverPluginInfo> Plugins { get; } = [];
        public IReadOnlyList<DriverDescriptor> Drivers => inner.Drivers.Concat(AdditionalDrivers).ToArray();
        public IDriverFactory GetFactory(string driverId) => inner.GetFactory(driverId);
        public IReadOnlyList<DriverPluginInfo> GetInstalledPlugins() => Plugins.ToArray();
        public string InstallPlugin(string sourcePath) => throw new NotSupportedException();
        public string UpdatePlugin(string driverId, string sourcePath, GatewayConfiguration configuration) => throw new NotSupportedException();
        public string UninstallPlugin(string driverId) => throw new NotSupportedException();
        public IReadOnlyList<string> GetRelatedDriverIds(string driverId) => [driverId];
        public bool IsBundledDriver(string driverId) => false;
    }

    private sealed class MemoryConfigurationStore(GatewayConfiguration initial) : IConfigurationStore, IDriverInventoryStore
    {
        public IReadOnlyList<DriverInventoryRecord> Inventory { get; set; } = [];
        public Task<IReadOnlyList<DriverInventoryRecord>> GetDriverInventoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(Inventory);
        public Task ReplaceDriverInventoryAsync(IReadOnlyList<DriverInventoryRecord> drivers, CancellationToken cancellationToken = default)
        { Inventory = drivers.ToArray(); return Task.CompletedTask; }
        private GatewayConfiguration _saved = GatewayJson.Clone(initial);
        public int Saves;
        public bool FailSave;
        public Task? BlockNextSave;
        public Action? AfterSave;
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(GatewayJson.Clone(_saved));
        public async Task SaveAsync(GatewayConfiguration configuration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocking = BlockNextSave;
            BlockNextSave = null;
            SaveEntered.TrySetResult();
            if (blocking is not null) await blocking.WaitAsync(cancellationToken);
            if (FailSave) throw new IOException("controlled save failure");
            cancellationToken.ThrowIfCancellationRequested();
            _saved = GatewayJson.Clone(configuration);
            Interlocked.Increment(ref Saves);
            AfterSave?.Invoke();
        }
    }

    private sealed class CountingFactory(MemoryConfigurationStore configurationStore) : IDriverFactory
    {
        private readonly SimulatorDriverFactory _inner = new();
        public ConcurrentDictionary<Guid, int> Created { get; } = new();
        public ConcurrentDictionary<Guid, int> Disposed { get; } = new();
        public ConcurrentBag<bool> CreatedAfterSave { get; } = [];
        public DriverDescriptor Descriptor => _inner.Descriptor;
        public IReadOnlyList<string> Validate(DeviceConfiguration configuration) => _inner.Validate(configuration);
        public IDeviceDriver Create(DeviceConfiguration configuration)
        {
            Created.AddOrUpdate(configuration.Id, 1, (_, count) => count + 1);
            CreatedAfterSave.Add(configurationStore.Saves > 0);
            return new CountedDriver(_inner.Create(configuration), () => Disposed.AddOrUpdate(configuration.Id, 1, (_, count) => count + 1));
        }
    }

    private sealed class CountedDriver(IDeviceDriver inner, Action disposed) : IDeviceDriver
    {
        public Task ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);
        public Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken) => inner.ReadAsync(tags, cancellationToken);
        public Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken) => inner.WriteAsync(tag, value, cancellationToken);
        public async ValueTask DisposeAsync() { await inner.DisposeAsync(); disposed(); }
    }

    private sealed class NoNetworkPublisher : ITelemetryPublisher
    {
        public bool IsConnected => false;
        public Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Application fixtures must never enable network publication.");
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
