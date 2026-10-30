namespace EdgeGateway.Tests.Application;

public sealed partial class GatewayApplicationTests
{
    [Fact]
    public async Task BatchDisableCommitsOnceAndLeavesUnselectedSessionRunning()
    {
        await using var h = await Harness.CreateAsync();
        var third = Device("未勾选设备");
        await h.App.AddDeviceAsync(third);
        foreach (var id in new[] { h.A.Id, h.B.Id, third.Id }) await h.App.StartDeviceAsync(id);
        var saves = h.Configurations.Saves;
        var result = await h.App.SetDevicesEnabledAsync([h.A.Id, h.B.Id, h.A.Id], false);
        Assert.Equal(saves + 1, h.Configurations.Saves);
        Assert.Equal(2, result.Stopped.Count);
        Assert.False(h.App.IsDeviceRunning(h.A.Id));
        Assert.False(h.App.IsDeviceRunning(h.B.Id));
        Assert.True(h.App.IsDeviceRunning(third.Id));
        Assert.Equal(1, h.Factory.Created[third.Id]);
        Assert.False(h.Factory.Disposed.ContainsKey(third.Id));
        Assert.All((await h.App.ReadSavedConfigurationAsync()).Devices.Where(d => d.Id != third.Id), d => Assert.False(d.Enabled));
        await h.App.SetDevicesEnabledAsync([h.A.Id, h.B.Id], true);
        Assert.All(h.App.GetConfiguration().Devices, d => Assert.True(d.Enabled));
        Assert.False(h.App.IsDeviceRunning(h.A.Id));
        Assert.False(h.App.IsDeviceRunning(h.B.Id));
        Assert.True(h.App.IsDeviceRunning(third.Id));
    }

    [Fact]
    public async Task BatchDisableSaveFailureLeavesAllTargetsAndSavedConfigurationIntact()
    {
        await using var h = await Harness.CreateAsync();
        await h.App.StartDeviceAsync(h.A.Id);
        await h.App.StartDeviceAsync(h.B.Id);
        var before = Json(h.App.GetConfiguration());
        h.Configurations.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => h.App.SetDevicesEnabledAsync([h.A.Id, h.B.Id], false));
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(before, Json(await h.App.ReadSavedConfigurationAsync()));
        Assert.True(h.App.IsDeviceRunning(h.A.Id));
        Assert.True(h.App.IsDeviceRunning(h.B.Id));
        Assert.Empty(h.Factory.Disposed);
    }

    [Fact]
    public async Task BatchEnableRejectsMissingTargetWithoutPartiallyChangingOthers()
    {
        await using var h = await Harness.CreateAsync();
        var before = Json(h.App.GetConfiguration());
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.SetDevicesEnabledAsync([h.A.Id, Guid.NewGuid()], false));
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(0, h.Configurations.Saves);
    }

    [Fact]
    public async Task BatchEnableUsesLatestCandidateAfterConcurrentEdit()
    {
        await using var h = await Harness.CreateAsync();
        var edited = h.App.GetConfiguration().Devices[0];
        edited.Name = "保留最新名称";
        edited.Tags[0].Name = "保留最新点位";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Configurations.BlockNextSave = release.Task;
        var edit = h.App.UpdateDeviceAsync(edited);
        await h.Configurations.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ids = new[] { h.A.Id };
        var batch = h.App.SetDevicesEnabledAsync(ids, false);
        ids[0] = h.B.Id;
        release.SetResult();
        await Task.WhenAll(edit, batch);
        var saved = await h.App.ReadSavedConfigurationAsync();
        var device = saved.Devices.Single(d => d.Id == h.A.Id);
        Assert.Equal("保留最新名称", device.Name);
        Assert.Equal("保留最新点位", device.Tags[0].Name);
        Assert.False(device.Enabled);
        Assert.True(saved.Devices.Single(d => d.Id == h.B.Id).Enabled);
    }

    [Fact]
    public async Task BatchCopyUsesFreshIdentitiesAndOneSaveWithoutChangingOriginals()
    {
        await using var h = await Harness.CreateAsync();
        await h.App.StartDeviceAsync(h.A.Id);
        var before = h.App.GetConfiguration();
        var result = await h.App.CopyDevicesAsync([h.A.Id, h.B.Id, h.A.Id]);
        Assert.Equal(2, result.Added.Count);
        Assert.Equal(1, h.Configurations.Saves);
        var saved = await h.App.ReadSavedConfigurationAsync();
        Assert.Equal(4, saved.Devices.Count);
        Assert.Equal(Json(before.Devices), Json(saved.Devices.Where(d => !result.Added.Contains(d.Id)).ToList()));
        var oldTags = before.Devices.SelectMany(d => d.Tags).Select(t => t.Id).ToHashSet();
        Assert.All(saved.Devices.Where(d => result.Added.Contains(d.Id)), copy =>
        {
            Assert.False(copy.Enabled);
            Assert.False(h.App.IsDeviceRunning(copy.Id));
            Assert.All(copy.Tags, tag => Assert.DoesNotContain(tag.Id, oldTags));
        });
        Assert.True(h.App.IsDeviceRunning(h.A.Id));
        Assert.Empty(h.Factory.Disposed);
        await h.App.CopyDevicesAsync([h.A.Id]);
        var names = h.App.GetConfiguration().Devices.Select(d => d.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task BatchDeleteSavesOnceAndStopsOnlyTargets()
    {
        await using var h = await Harness.CreateAsync();
        var third = Device("保留设备");
        await h.App.AddDeviceAsync(third);
        foreach (var id in new[] { h.A.Id, h.B.Id, third.Id }) await h.App.StartDeviceAsync(id);
        var saves = h.Configurations.Saves;
        var result = await h.App.DeleteDevicesAsync([h.A.Id, h.B.Id, h.A.Id]);
        Assert.Equal(2, result.Removed.Count);
        Assert.Equal(saves + 1, h.Configurations.Saves);
        Assert.Equal(third.Id, Assert.Single((await h.App.ReadSavedConfigurationAsync()).Devices).Id);
        Assert.False(h.App.IsDeviceRunning(h.A.Id));
        Assert.False(h.App.IsDeviceRunning(h.B.Id));
        Assert.True(h.App.IsDeviceRunning(third.Id));
        Assert.False(h.Factory.Disposed.ContainsKey(third.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BatchCopyDeleteRejectMissingTargetsAndSaveFailureAtomically(bool copy)
    {
        await using var h = await Harness.CreateAsync();
        await h.App.StartDeviceAsync(h.A.Id);
        var before = Json(h.App.GetConfiguration());
        Task Execute(Guid[] ids) => copy ? h.App.CopyDevicesAsync(ids) : h.App.DeleteDevicesAsync(ids);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute([h.A.Id, Guid.NewGuid()]));
        Assert.Equal(0, h.Configurations.Saves);
        h.Configurations.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => Execute([h.A.Id, h.B.Id]));
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(before, Json(await h.App.ReadSavedConfigurationAsync()));
        Assert.True(h.App.IsDeviceRunning(h.A.Id));
        Assert.Empty(h.Factory.Disposed);
    }
}
