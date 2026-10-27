using EdgeGateway.Model;

namespace EdgeGateway.Tests.Application;

public sealed partial class GatewayApplicationTests
{
    [Fact]
    public async Task DeviceGroupsAddedByLiveDeviceEditsDoNotRestartOtherDevices()
    {
        await using var h = await Harness.CreateAsync();
        await h.App.StartDeviceAsync(h.A.Id);
        await Until(() => h.App.GetLatest().Count > 0);
        var device = h.App.GetConfiguration().Devices.Single(d => d.Id == h.B.Id);
        device.Group = "运行中加入的新分组";
        await h.App.UpdateDeviceAsync(device);
        var added = Device("运行中新增设备");
        added.Group = "另一新分组";
        await h.App.AddDeviceAsync(added);
        Assert.Contains(device.Group, h.App.GetConfiguration().DeviceGroups);
        Assert.Contains(added.Group, (await h.App.ReadSavedConfigurationAsync()).DeviceGroups);
        Assert.True(h.App.IsDeviceRunning(h.A.Id));
        Assert.False(h.App.IsDeviceRunning(added.Id));
        Assert.Equal(1, h.Factory.Created[h.A.Id]);
        Assert.False(h.Factory.Disposed.ContainsKey(h.A.Id));
    }

    [Fact]
    public async Task DeviceGroupsCreateRenameAndDeleteWithReferencesPreserved()
    {
        await using var h = await Harness.CreateAsync();
        await h.App.SaveDeviceGroupAsync(null, " 空分组 ");
        Assert.Contains("空分组", (await h.App.ReadSavedConfigurationAsync()).DeviceGroups);
        var device = h.App.GetConfiguration().Devices[0];
        var tagIds = device.Tags.Select(t => t.Id).ToArray();
        device.Group = "空分组";
        await h.App.UpdateDeviceAsync(device);
        await h.App.SaveDeviceGroupAsync("空分组", "生产线 A");
        var updated = h.App.GetConfiguration().Devices.Single(d => d.Id == device.Id);
        Assert.Equal("生产线 A", updated.Group);
        Assert.Equal(tagIds, updated.Tags.Select(t => t.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.DeleteDeviceGroupAsync("生产线 a"));
        await h.App.DeleteDeviceAsync(device.Id);
        Assert.Contains("生产线 A", (await h.App.ReadSavedConfigurationAsync()).DeviceGroups);
        await h.App.DeleteDeviceGroupAsync("生产线 a");
        Assert.DoesNotContain("生产线 A", h.App.GetConfiguration().DeviceGroups);
    }

    [Fact]
    public async Task DeviceGroupsRejectDuplicatesAndFailedSaveRetainsAppliedAndSavedValues()
    {
        await using var h = await Harness.CreateAsync();
        await h.App.SaveDeviceGroupAsync(null, "Line A");
        await h.App.SaveDeviceGroupAsync(null, "Line B");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.SaveDeviceGroupAsync(null, " line a "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.SaveDeviceGroupAsync("Line B", "Line A"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.SaveDeviceGroupAsync("missing", "Line C"));
        await Assert.ThrowsAsync<ArgumentException>(() => h.App.SaveDeviceGroupAsync(null, " "));
        var before = Json(h.App.GetConfiguration());
        h.Configurations.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => h.App.SaveDeviceGroupAsync("Line A", "Renamed"));
        await Assert.ThrowsAsync<IOException>(() => h.App.DeleteDeviceGroupAsync("Line B"));
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(before, Json(await h.App.ReadSavedConfigurationAsync()));
        h.Configurations.FailSave = false;
        await h.App.SaveDeviceGroupAsync("Line A", "Renamed");
        await h.App.StartDeviceAsync(h.A.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.SaveDeviceGroupAsync(null, "Running"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.DeleteDeviceGroupAsync("Line B"));
    }

    [Fact]
    public async Task ChannelCatalogCrudRejectsReferencesDuplicatesAndFailedSaves()
    {
        await using var h = await Harness.CreateAsync();
        var channel = new ChannelConfiguration { Name = "Line A", InterRequestDelayMs = 20 };
        await h.App.UpsertChannelAsync(channel, true);
        channel.Name = "Line B"; channel.Description = "Shared bus"; channel.InterRequestDelayMs = 40;
        await h.App.UpsertChannelAsync(channel, false);
        Assert.Equal(40, Assert.Single(h.App.GetConfiguration().Channels).InterRequestDelayMs);
        await Assert.ThrowsAnyAsync<Exception>(() => h.App.UpsertChannelAsync(new() { Name = "line b" }, true));
        var device = h.App.GetConfiguration().Devices[0];
        device.ChannelId = channel.Id;
        await h.App.UpdateDeviceAsync(device);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.DeleteChannelAsync(channel.Id));
        device.ChannelId = null;
        await h.App.UpdateDeviceAsync(device);
        var before = Json(h.App.GetConfiguration());
        h.Configurations.FailSave = true;
        channel.Name = "Should not save";
        await Assert.ThrowsAsync<IOException>(() => h.App.UpsertChannelAsync(channel, false));
        await Assert.ThrowsAsync<IOException>(() => h.App.DeleteChannelAsync(channel.Id));
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(before, Json(await h.App.ReadSavedConfigurationAsync()));
        h.Configurations.FailSave = false;
        await h.App.DeleteChannelAsync(channel.Id);
        Assert.Empty((await h.App.ReadSavedConfigurationAsync()).Channels);
    }
}
