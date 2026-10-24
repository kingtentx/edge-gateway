using EdgeGateway.Application;
using EdgeGateway.Model;
using System.Diagnostics;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeDeviceActionsAndAutosaveAsync()
    {
        if (!_smokeTest || _application.IsRunning)
            throw new InvalidOperationException("Device-action verification requires an isolated, stopped smoke-test session.");
        var original = Clone(_configuration);
        var originalDirty = _dirty;
        var originalSelection = SelectedDevice()?.Id;
        var originalPage = _activePage;
        var originalFooter = _lastFeedback;
        var originalFilter = (_realtimeDevice.SelectedItem as Choice)?.Id;
        var originalSearch = _realtimeSearch.Text;
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Device-action smoke: " + message);
        }
        async Task WaitFor(Func<bool> condition, string message)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Check(condition(), message);
        }
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false;
            fixture.Mqtt.Enabled = false;
            fixture.Devices = new[] { "A", "B" }.Select(name => new DeviceConfiguration
            {
                Name = "独立采集验证 " + name, DriverId = "simulator", PollIntervalMs = 100,
                Tags = [new TagConfiguration { Name = "模拟斜坡", Address = "ramp", DataType = TagDataType.Float64, Writable = true }]
            }).ToList();
            var a = fixture.Devices[0].Id;
            var b = fixture.Devices[1].Id;
            await CommitConfigurationAsync(fixture, a, replaceCloudDraft: true);
            await AssertSavedConfigurationForSmokeAsync("simulator fixture");
            ShowPage("devices");
            await DeviceRowActionAsync(b, DeviceStartColumnName);
            await WaitFor(() => _application.GetLatest().Any(x => x.DeviceId == b && x.Quality == SampleQuality.Good), "B did not collect.");
            Check(SelectedDevice()?.Id == a && !_application.IsDeviceRunning(a) && _application.IsDeviceRunning(b)
                && _application.GetLatest().All(x => x.DeviceId == b), "starting row B redirected to selected A or started another device.");
            await DeviceRowActionAsync(a, DeviceStartColumnName);
            await DeviceRowActionAsync(a, DeviceStartColumnName);
            await WaitFor(() => _application.GetLatest().Count(x => x.Quality == SampleQuality.Good) == 2, "both devices did not collect.");
            Check(_application.IsDeviceRunning(a) && _application.IsDeviceRunning(b), "both sessions must be active.");
            UpdateCommands();
            VerifyDeviceRowCommandsForSmoke("two running devices");
            SaveEditorScreenshot(this, "devices", "running");

            await DeviceRowActionAsync(b, DeviceStopColumnName);
            var lastA = _application.GetLatest().Single(x => x.DeviceId == a).Timestamp;
            await WaitFor(() => _application.GetLatest().Any(x => x.DeviceId == a && x.Timestamp > lastA && x.Quality == SampleQuality.Good), "A stopped sampling when B stopped.");
            Check(_application.IsRunning && _application.IsDeviceRunning(a) && !_application.IsDeviceRunning(b)
                && _application.GetLatest().Single(x => x.DeviceId == b).Quality == SampleQuality.Uncertain, "B's stopped snapshot or A's running state is incorrect.");
            ShowPage("realtime");
            SelectChoice(_realtimeDevice, b); _realtimeSearch.Text = "";
            RefreshRealtime(_application.GetLatest());
            Check(_realtimeGrid.Rows.Count == 1, "stopped B must retain its visible last sample.");
            _realtimeGrid.CurrentCell = _realtimeGrid.Rows[0].Cells[0]; UpdateWriteButton();
            Check(!_writeButton.Enabled, "stopped B still allows point writes while A runs.");
            SelectChoice(_realtimeDevice, a); RefreshRealtime(_application.GetLatest());
            Check(_writeButton.Enabled, "running A lost point-write availability when B stopped.");
            await DeviceRowActionAsync(a, DeviceStopColumnName);
            Check(!_application.IsRunning, "last device did not finish stopping shared services.");
            ShowPage("devices"); UpdateCommands();
            VerifyDeviceRowCommandsForSmoke("all devices stopped");
            SaveEditorScreenshot(this, "devices", "stopped");

            var disabled = Clone(_configuration); disabled.Devices[1].Enabled = false;
            await CommitConfigurationAsync(disabled, a);
            await DeviceRowActionAsync(b, DeviceStartColumnName);
            Check(!_application.IsRunning, "disabled row started collection.");
            var before = DeviceImportSmokeJson(_configuration);
            var diskBefore = DeviceImportSmokeJson(await _application.ReadSavedConfigurationAsync());
            var rejected = false;
            var candidate = Clone(_configuration); candidate.Name += " 保存失败验证";
            using (RejectConfigurationWritesForSmoke())
            {
                try { await CommitConfigurationAsync(candidate, a); }
                catch (Exception error) when (IsConfigurationWriteFailureForSmoke(error)) { rejected = true; }
                Check(rejected && DeviceImportSmokeJson(_configuration) == before, "failed disk save changed the UI configuration.");
            }
            Check(diskBefore == DeviceImportSmokeJson(await _application.ReadSavedConfigurationAsync()), "failed save changed the persisted SQLite configuration.");
            candidate = Clone(_configuration); candidate.Devices[0].PollIntervalMs = 0; rejected = false;
            try { await CommitConfigurationAsync(candidate, a); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && DeviceImportSmokeJson(_configuration) == before
                && diskBefore == DeviceImportSmokeJson(await _application.ReadSavedConfigurationAsync()), "invalid configuration changed memory or disk.");
            await AssertSavedConfigurationForSmokeAsync("rejected configuration saves");

            ShowPage("cloud");
            var nameField = (TextBox)_cloudFields["name"];
            var draftName = _configuration.Name + " 平台草稿";
            nameField.Text = draftName;
            Check(_dirty && DeviceImportSmokeJson(_configuration) == before, "typing platform settings mutated applied configuration.");
            await AssertSavedConfigurationForSmokeAsync("unapplied platform draft");
            candidate = Clone(_configuration); candidate.Devices[0].Group = "草稿期间已保存设备";
            await CommitConfigurationAsync(candidate, a);
            Check(_dirty && nameField.Text == draftName && _configuration.Name != draftName, "device save replaced the unapplied platform draft.");
            await AssertSavedConfigurationForSmokeAsync("device save with platform draft");
            await ApplyCloudSettingsAsync();
            Check(!_dirty && _configuration.Name == draftName && nameField.Text == draftName
                && _configuration.Devices[0].Group == "草稿期间已保存设备", "applying platform settings reverted newer device edits.");
            await AssertSavedConfigurationForSmokeAsync("applied platform draft");
            Console.WriteLine("Device-action/autosave smoke passed: row identity, independent collection, stopped-value quality/write protection, disabled start, atomic save rejection, platform draft preservation; only simulators ran and MQTT remained disabled.");
        }
        finally
        {
            await _application.StopAsync();
            await RestoreConfigurationForSmokeAsync(original);
            _dirty = originalDirty;
            RebindConfigurationViews(originalSelection);
            SelectChoice(_realtimeDevice, originalFilter); _realtimeSearch.Text = originalSearch;
            ShowPage(originalPage); SetFooter(originalFooter); UpdateCommands();
        }
    }

    private void VerifyDeviceRowCommandsForSmoke(string context)
    {
        if (_deviceGrid.Rows.Count == 0) throw new InvalidOperationException(context + ": no device row is available to verify.");
        var row = _deviceGrid.Rows[0];
        foreach (var name in new[] { DeviceStartColumnName, DeviceStopColumnName, DeviceConfigurationColumnName })
        {
            var cell = row.Cells[name];
            _deviceGrid.CurrentCell = cell;
            _deviceGrid.FirstDisplayedScrollingRowIndex = row.Index;
            _deviceGrid.FirstDisplayedScrollingColumnIndex = cell.ColumnIndex;
            FocusForSmoke(_deviceGrid, _deviceGrid, this, context + " " + name);
            PerformLayoutRecursively(this);
            // The table may be taller than a small workspace. Scroll each real
            // ancestor to the requested cell, never require the entire table to fit.
            for (var pass = 0; pass < 2; pass++)
            for (var parent = _deviceGrid.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is not ScrollableControl scroll || !scroll.AutoScroll) continue;
                var target = _deviceGrid.RectangleToScreen(_deviceGrid.GetCellDisplayRectangle(cell.ColumnIndex, row.Index, false));
                var viewport = scroll.RectangleToScreen(scroll.ClientRectangle);
                var dx = target.Left < viewport.Left ? target.Left - viewport.Left : Math.Max(0, target.Right - viewport.Right);
                var dy = target.Top < viewport.Top ? target.Top - viewport.Top : Math.Max(0, target.Bottom - viewport.Bottom);
                if (dx != 0 || dy != 0)
                {
                    scroll.AutoScrollPosition = new Point(Math.Max(0, -scroll.AutoScrollPosition.X + dx), Math.Max(0, -scroll.AutoScrollPosition.Y + dy));
                    PerformLayoutRecursively(this);
                }
            }
            var bounds = _deviceGrid.GetCellDisplayRectangle(cell.ColumnIndex, row.Index, false);
            var clipped = _deviceGrid.GetCellDisplayRectangle(cell.ColumnIndex, row.Index, true);
            if (!_deviceGrid.ContainsFocus || _deviceGrid.CurrentCell != cell || bounds.Width <= 0 || bounds.Height <= 0 || bounds != clipped)
                throw new InvalidOperationException($"{context}: {name} cannot be focused and fully displayed inside its grid: {bounds}, clipped {clipped}.");
            var screenBounds = _deviceGrid.RectangleToScreen(bounds);
            for (Control? parent = _deviceGrid; parent is not null; parent = parent.Parent)
                if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(screenBounds))
                    throw new InvalidOperationException($"{context}: {name} is clipped by {parent.Name}: {screenBounds}.");
            var inset = UiTheme.Scale(_deviceGrid, cell is DeviceActionButtonCell ? 5 : 2);
            var text = TextRenderer.MeasureText(cell.FormattedValue?.ToString() ?? "", cell.InheritedStyle.Font ?? _deviceGrid.Font, Size.Empty, TextFormatFlags.SingleLine);
            if (text.Width > bounds.Width - 2 * inset || text.Height > bounds.Height - 2 * inset)
                throw new InvalidOperationException($"{context}: {name} text {text} does not fit its cell {bounds.Size}.");
        }
        foreach (DataGridViewRow item in _deviceGrid.Rows)
        {
            if (item.DataBoundItem is not DeviceRow data) continue;
            var device = _configuration.Devices.Single(x => x.Id == data.Id);
            var running = _application.IsDeviceRunning(device.Id);
            if (((DeviceActionButtonCell)item.Cells[DeviceStartColumnName]).ActionEnabled != (!_busy && !_closing && device.Enabled && !running)
                || ((DeviceActionButtonCell)item.Cells[DeviceStopColumnName]).ActionEnabled != (!_busy && !_closing && running))
                throw new InvalidOperationException(context + ": row command availability disagrees with its device session.");
        }
    }

    private async Task AssertSavedConfigurationForSmokeAsync(string context)
    {
        var saved = await _application.ReadSavedConfigurationAsync();
        if (JsonSerializer.Serialize(saved, GatewayJson.Options)
            != JsonSerializer.Serialize(_configuration, GatewayJson.Options))
            throw new InvalidOperationException(context + ": saved configuration differs from the applied UI configuration.");
    }

    private async Task RestoreConfigurationForSmokeAsync(GatewayConfiguration original)
    {
        if (_application.IsRunning) throw new InvalidOperationException("Stop smoke collection before restoring configuration.");
        await _application.ReplaceConfigurationAsync(original);
        _configuration = _application.GetConfiguration();
        ResetCloudDraft();
    }
}
