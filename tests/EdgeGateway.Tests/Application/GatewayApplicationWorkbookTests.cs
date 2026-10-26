using EdgeGateway.Model;
using EdgeGateway.Application;

namespace EdgeGateway.Tests.Application;

public sealed partial class GatewayApplicationTests
{
    [Fact]
    public async Task WorkbookPreviewDoesNotSaveAndImportIncludesChannelAndTags()
    {
        await using var h = await Harness.CreateAsync();
        var channel = new ChannelConfiguration { Name = "导入通道" };
        var device = Device("导入设备"); device.ChannelId = channel.Id;
        var workbook = new DeviceConfigurationWorkbook { Channels = [channel], Devices = [device] };
        var preview = h.App.PreviewDeviceWorkbook(workbook, false);
        Assert.Empty(preview.Errors);
        Assert.Equal(1, preview.NewChannels);
        Assert.Equal("新增", Assert.Single(preview.Devices).Change);
        Assert.Equal(0, h.Configurations.Saves);
        await h.App.ImportDeviceWorkbookAsync(workbook, false);
        var saved = await h.App.ReadSavedConfigurationAsync();
        Assert.Equal(3, saved.Devices.Count);
        Assert.Equal(channel.Id, saved.Devices[2].ChannelId);
        Assert.Equal(device.Tags[0].Id, saved.Devices[2].Tags[0].Id);
        Assert.False(h.App.IsDeviceRunning(device.Id));
    }

    [Fact]
    public async Task WorkbookUpdateMergesAgainstLatestAndRetainsUnlistedData()
    {
        await using var h = await Harness.CreateAsync();
        var imported = h.App.GetConfiguration().Devices[0]; imported.Name = "修改后的设备";
        var workbook = new DeviceConfigurationWorkbook { Devices = [imported] };
        Assert.Empty(h.App.PreviewDeviceWorkbook(workbook, true).Errors);
        var latest = h.App.GetConfiguration().Devices[0];
        var extra = new TagConfiguration { Name = "后来添加", Address = "constant:3", DataType = TagDataType.Float64 };
        latest.Tags.Add(extra); latest.Properties["保留参数"] = "value";
        await h.App.UpdateDeviceAsync(latest);
        await h.App.ImportDeviceWorkbookAsync(workbook, true);
        var result = h.App.GetConfiguration();
        Assert.Equal("修改后的设备", result.Devices[0].Name);
        Assert.Contains(result.Devices[0].Tags, tag => tag.Id == extra.Id);
        Assert.Equal("value", result.Devices[0].Properties["保留参数"]);
        Assert.Equal(h.B.Id, result.Devices[1].Id);
        Assert.Equal(h.A.Tags[0].Id, result.Devices[0].Tags[0].Id);
    }

    [Fact]
    public async Task WorkbookDuplicateAndInvalidDriverAreRejectedWithoutSaving()
    {
        await using var h = await Harness.CreateAsync();
        var before = Json(h.App.GetConfiguration());
        var workbook = new DeviceConfigurationWorkbook { Devices = [h.A] };
        Assert.NotEmpty(h.App.PreviewDeviceWorkbook(workbook, false).Errors);
        await Assert.ThrowsAsync<InvalidDataException>(() => h.App.ImportDeviceWorkbookAsync(workbook, false));
        var missing = Device("缺少驱动"); missing.DriverId = "missing";
        await Assert.ThrowsAnyAsync<Exception>(() => h.App.ImportDeviceWorkbookAsync(new() { Devices = [missing] }, false));
        Assert.Equal(0, h.Configurations.Saves);
        Assert.Equal(before, Json(h.App.GetConfiguration()));
    }

    [Fact]
    public async Task WorkbookSaveFailureAndCancellationLeaveConfigurationUntouched()
    {
        await using var h = await Harness.CreateAsync();
        var before = Json(h.App.GetConfiguration());
        var workbook = new DeviceConfigurationWorkbook { Devices = [Device("新增")] };
        h.Configurations.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => h.App.ImportDeviceWorkbookAsync(workbook, false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.App.ImportDeviceWorkbookAsync(workbook, false, new(true)));
        Assert.Equal(before, Json(h.App.GetConfiguration()));
        Assert.Equal(0, h.Configurations.Saves);
    }

    [Fact]
    public async Task WorkbookConflictingChannelCannotChangeOtherDevices()
    {
        await using var h = await Harness.CreateAsync();
        var channel = new ChannelConfiguration { Name = "原通道" };
        await h.App.UpsertChannelAsync(channel, true);
        var changed = GatewayJson.Clone(channel); changed.InterRequestDelayMs = 123;
        var device = Device("导入设备"); device.ChannelId = channel.Id;
        var workbook = new DeviceConfigurationWorkbook { Devices = [device], Channels = [changed] };
        await Assert.ThrowsAsync<InvalidDataException>(() => h.App.ImportDeviceWorkbookAsync(workbook, false));
        Assert.Equal(0, h.App.GetConfiguration().Channels[0].InterRequestDelayMs);
        Assert.Equal(2, h.App.GetConfiguration().Devices.Count);
    }

    [Fact]
    public async Task WorkbookNewChannelRequiresStopWhileDeviceOnlyImportPreservesRunningPeer()
    {
        await using var h = await Harness.CreateAsync();
        await h.App.StartDeviceAsync(h.A.Id);
        await Until(() => h.Factory.Created.ContainsKey(h.A.Id));
        var workbook = new DeviceConfigurationWorkbook { Devices = [Device("新设备")], Channels = [new() { Name = "新通道" }] };
        Assert.NotEmpty(h.App.PreviewDeviceWorkbook(workbook, false).Errors);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.App.ImportDeviceWorkbookAsync(workbook, false));
        workbook.Channels.Clear();
        await h.App.ImportDeviceWorkbookAsync(workbook, false);
        Assert.True(h.App.IsDeviceRunning(h.A.Id));
        Assert.False(h.App.IsDeviceRunning(workbook.Devices[0].Id));
        Assert.Equal(1, h.Factory.Created[h.A.Id]);
    }

    [Fact]
    public async Task WorkbookFilesThroughApplicationPreserveIdsAndSelectedScope()
    {
        await using var h = await Harness.CreateAsync();
        var path = Path.Combine(h.Directory, "设备.xlsx");
        await h.App.ExportDeviceWorkbookAsync(path, h.A.Id);
        var workbook = await h.App.ReadDeviceWorkbookAsync(path);
        Assert.Equal(h.A.Id, Assert.Single(workbook.Devices).Id);
        Assert.Equal(h.A.Tags[0].Id, workbook.Devices[0].Tags[0].Id);
        var pointPath = Path.Combine(h.Directory, "点位.xlsx");
        await h.App.ExportTagWorkbookAsync(pointPath, h.A.Id);
        var content = await h.App.ReadTagWorkbookAsync(pointPath);
        await h.App.ImportTagFileAsync(h.B.Id, content, TagCsvImportMode.UpdateByName);
        Assert.Equal(h.B.Tags[0].Id, h.App.GetConfiguration().Devices[1].Tags[0].Id);
    }

    [Fact]
    public async Task WorkbookPointImportRetainsPointAddedAfterPreview()
    {
        await using var h = await Harness.CreateAsync();
        var bytes = TagCsvExchange.Export(h.A.Tags);
        var extra = new TagConfiguration { Name = "新点", Address = "constant:4", DataType = TagDataType.Float64 };
        await h.App.AddTagAsync(h.A.Id, extra);
        await h.App.ImportTagFileAsync(h.A.Id, bytes, TagCsvImportMode.UpdateByName);
        Assert.Equal(2, h.App.GetConfiguration().Devices[0].Tags.Count);
        Assert.Contains(h.App.GetConfiguration().Devices[0].Tags, tag => tag.Id == extra.Id);
    }
}
