using EdgeGateway.Model;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool DeviceBatchSmoke { get; set; }

    private async Task SmokeDeviceBatchAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("Batch smoke requires an isolated stopped host.");
        _refreshTimer.Stop();
        var original = Clone(_configuration);
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt.Enabled = false;
            fixture.Databases = []; fixture.DatabaseWriteGroups = []; fixture.Channels = [];
            fixture.Devices = Enumerable.Range(1, 13).Select(i => new DeviceConfiguration
            {
                Name = $"批量检查 {i:00}", DriverId = "simulator", Enabled = i != 2,
                Tags = [new TagConfiguration { Name = "数值", Address = "constant:1", DataType = TagDataType.Float64 }]
            }).ToList();
            await CommitConfigurationAsync(fixture, replaceCloudDraft: true);
            ShowPage("devices"); PerformLayoutRecursively(this); await Task.Delay(150);
            var pager = GridPagination.GetPager(_deviceGrid);
            var first = fixture.Devices[0].Id; var last = fixture.Devices[10].Id;
            var independent = fixture.Devices[12].Id;
            var check = _deviceGrid.Rows[0].Cells[0];
            Require(check.RenderedCell is AntdUI.CellCheckbox, "First column is not an official checkbox.");
            // Exercise native mouse dispatch against the actual checkbox hit rectangle.
            var bounds = _deviceGrid.GetCellDisplayRectangle(0, 0, true);
            var position = new IntPtr(((bounds.Top + bounds.Height / 2) << 16) | (bounds.Left + bounds.Width / 2));
            SendControlSmokeMessage(_deviceGrid.Handle, 0x0201, new IntPtr(1), position);
            SendControlSmokeMessage(_deviceGrid.Handle, 0x0202, IntPtr.Zero, position);
            await Task.Delay(100);
            Require(_checkedDeviceIds.SetEquals([first]), "Mouse click did not check the event row.");
            pager.Current = 2; await Task.Delay(100);
            _deviceGrid.CurrentCell = _deviceGrid.Rows[0].Cells[0];
            SendControlSmokeMessage(_deviceGrid.Handle, 0x0100, new IntPtr((int)Keys.Space), IntPtr.Zero);
            Require(_checkedDeviceIds.SetEquals([first, last]), "Keyboard check did not preserve previous page.");
            await RefreshSnapshotsAsync(); BindDeviceRows();
            Require(_checkedDeviceIds.SetEquals([first, last]) && pager.Current == 2, "Refresh lost checked IDs or page.");
            var captured = BuildDeviceBatchMenu().Single(c => c.Key == "BatchStartDevices");
            _deviceGrid.CurrentCell = _deviceGrid.Rows[2].Cells["Name"];
            await _application.StartDeviceAsync(independent);
            await captured.Execute();
            Require(_application.IsDeviceRunning(first) && _application.IsDeviceRunning(last)
                && _application.IsDeviceRunning(independent), "Batch start targets changed with current row.");
            await RunDeviceBatchAsync([first, last], DeviceBatchAction.Disable);
            Require(!_application.IsDeviceRunning(first) && !_application.IsDeviceRunning(last)
                && _application.IsDeviceRunning(independent), "Batch disable affected an unselected device.");
            await RunDeviceBatchAsync([first, last], DeviceBatchAction.Enable);
            Require(!_application.IsDeviceRunning(first) && !_application.IsDeviceRunning(last), "Enabling auto-started devices.");
            var saved = JsonSerializer.Serialize(_configuration, GatewayJson.Options);
            using (_configurationWriteFailureForSmoke!())
            {
                var rejected = false;
                try { await RunDeviceBatchAsync([first, last], DeviceBatchAction.Disable); }
                catch { rejected = true; }
                Require(rejected && JsonSerializer.Serialize(_application.GetConfiguration(), GatewayJson.Options) == saved, "Failed save changed enabled configuration.");
            }
            await RunDeviceBatchAsync([first, fixture.Devices[1].Id, Guid.NewGuid()], DeviceBatchAction.Start);
            Require(_lastFeedback.Contains("成功 1 台，跳过 1 台，失败 1 台"), "Partial results were not reported accurately.");
            await RunDeviceBatchAsync([first, last], DeviceBatchAction.Stop);
            Require(!_application.IsDeviceRunning(first) && _application.IsDeviceRunning(independent), "Batch stop affected unselected session.");
            Require(_footer.ForeColor == UiTheme.Muted, "Zero failures incorrectly shows error color.");
            pager.Current = 1; await Task.Delay(100);
            _devicesPageView.deviceBatchButton.OpenMenu(); await Task.Delay(100);
            Require(_devicesPageView.deviceBatchButton.OpenedMenu.Visible
                && _devicesPageView.deviceBatchButton.OpenedCommands.Count == 6, "Batch menu did not open all six commands.");
            SaveEditorScreenshot(this, "device-batch", "checked-menu");
            SaveEditorScreenshot(_devicesPageView.deviceBatchButton.OpenedMenu, "device-batch", "menu-popup");
            _devicesPageView.deviceBatchButton.OpenedMenu.Close();
            await BuildDeviceBatchMenu().Single(c => c.Key == "CheckFilteredDevices").Execute();
            Require(_checkedDeviceIds.Count == 13, "Select filtered did not include all pages.");
            _deviceSearch.Text = "批量检查 01";
            Require(_checkedDeviceIds.SetEquals([first]), "Filtering retained hidden checked targets.");
            _deviceSearch.Clear();
            Require(_checkedDeviceIds.SetEquals([first]), "Clearing filter restored obsolete selections.");
            await BuildDeviceBatchMenu().Single(c => c.Key == "ClearCheckedDevices").Execute();
            Require(!BuildDeviceBatchMenu().Single(c => c.Key == "BatchStartDevices").CanExecute(), "Empty batch command enabled.");
            SaveEditorScreenshot(this, "device-batch", "cleared");
            Console.WriteLine($"Device batch smoke passed: official mouse/keyboard checkbox, cross-page selection, refresh, captured targets, start/stop/enable/disable isolation, failed save, partial results and filter cleanup; DeviceDpi={DeviceDpi}.");
        }
        finally
        {
            await _application.StopAsync();
            _checkedDeviceIds.Clear(); _deviceSearch.Clear();
            await RestoreConfigurationForSmokeAsync(original);
        }
    }
}
