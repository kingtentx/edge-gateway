using EdgeGateway.Model;
using EdgeGateway.Application;
using System.Diagnostics;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeLiveConfigurationAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("Live-configuration smoke requires stopped, isolated fixtures.");
        var original = Clone(_configuration);
        var originalPage = _activePage;
        var originalSelection = SelectedDevice()?.Id;
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Live-configuration smoke: " + message);
        }
        async Task WaitFor(Func<bool> predicate, string message)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
            Check(predicate(), message);
        }
        async Task AssertContinues(Guid id, DateTimeOffset before, string context) =>
            await WaitFor(() => _application.IsDeviceRunning(id) && _application.GetLatest().Any(sample =>
                sample.DeviceId == id && sample.Quality == SampleQuality.Good && sample.Timestamp > before), context);
        DateTimeOffset Latest(Guid id) => _application.GetLatest().First(sample => sample.DeviceId == id).Timestamp;
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt = new MqttConfiguration { Enabled = false, Platform = MqttPlatform.Generic };
            fixture.Devices = new[] { "在线维护 A", "在线维护 B" }.Select(name => new DeviceConfiguration
            {
                Name = name, DriverId = "simulator", PollIntervalMs = 100,
                Tags = [new TagConfiguration { Name = "模拟斜坡", Address = "ramp", DataType = TagDataType.Float64 }]
            }).ToList();
            var a = fixture.Devices[0].Id; var b = fixture.Devices[1].Id;
            await CommitConfigurationAsync(fixture, a, replaceCloudDraft: true);
            ShowPage("devices");
            await DeviceRowActionAsync(a, DeviceStartColumnName);
            await WaitFor(() => _application.GetLatest().Any(sample => sample.DeviceId == a && sample.Quality == SampleQuality.Good), "A did not collect.");
            Check(CanEdit() && SelectedDevice()?.Id == a && SelectedTag() is not null,
                "the running fixture lost its editable device/point context.");
            // The new UI intentionally disables fields for other platforms and a
            // apply/reset actions when no logging draft exists. Every remaining device, point and
            // platform-draft command must stay usable while A is collecting.
            Check(!_loggingDirty, "the running fixture unexpectedly inherited a logging draft.");
            var disabled = _stoppedConfigurationCommands.Concat(new Control[]
                { _cloudFields["typeKey"], _cloudFields["platformGatewayId"], _cloudReset,
                    _loggingApply, _loggingReset }).ToHashSet();
            foreach (var control in _configurationCommands)
                Check(control.Enabled == !disabled.Contains(control),
                    $"unexpected availability for '{control.Name}' / '{control.Text}' while A is collecting.");
            var stamp = Latest(a);
            var added = await SmokeDeviceConfigurationModalAsync(AddDeviceAsync, editor =>
            {
                editor.SetFieldForSmoke("Name", "运行期间新增 C");
                editor.SetFieldForSmoke("PollIntervalMs", "100");
            }, "add C while A runs");
            Check(_configuration.Devices.Count == 3 && !_application.IsDeviceRunning(added.Id), "new device unexpectedly started.");
            await AssertContinues(a, stamp, "adding C interrupted A.");

            RebindConfigurationViews(a);
            var rowB = _deviceGrid.Rows.Cast<GatewayTableRow>().Single(row => row.DataBoundItem is DeviceRow data && data.Id == b);
            stamp = Latest(a);
            await SmokeDeviceConfigurationModalAsync(() => DeviceGridActionAsync(
                new DataGridViewCellEventArgs(_deviceGrid.Columns["Name"].Index, rowB.Index), doubleClick: true), editor =>
            {
                editor.SetFieldForSmoke("Name", "在线维护 B 已修改");
                editor.SetFieldForSmoke("PollIntervalMs", "150");
            }, "edit stopped B through double-click while A runs");
            Check(_configuration.Devices.Single(device => device.Id == b).PollIntervalMs == 150 && !_application.IsDeviceRunning(b), "stopped B edit did not stay stopped.");
            await AssertContinues(a, stamp, "editing B interrupted A.");
            await DeviceRowActionAsync(b, DeviceStartColumnName);
            await WaitFor(() => _application.GetLatest().Any(sample => sample.DeviceId == b && sample.Quality == SampleQuality.Good), "B did not start using saved settings.");

            stamp = Latest(a);
            await SmokeDeviceConfigurationModalAsync(() => ConfigureDeviceAsync(b), editor =>
            {
                Check(editor.SmokeCommands.OfType<Button>().Any(button => button.Text == "保存并应用"), "running target did not explain apply behavior.");
                editor.SetFieldForSmoke("Name", "在线维护 B 已重连");
            }, "apply running B only");
            await WaitFor(() => _application.GetLatest().Any(sample => sample.DeviceId == b && sample.DeviceName == "在线维护 B 已重连" && sample.Quality == SampleQuality.Good), "B did not use its applied configuration.");
            await AssertContinues(a, stamp, "reconfiguring B interrupted A.");
            await SmokeRetainedDeviceInputAsync(b);

            var csv = TagCsvExchange.Export(new[] { new TagConfiguration
                { Name = "仅本地采集", Address = "constant:25.5", DataType = TagDataType.Float64, UploadEnabled = false } });
            stamp = Latest(a);
            using (var import = CreateTagImportDialog(_configuration.Devices.Single(device => device.Id == b), csv))
            {
                import.Show(this);
                VerifyEditorLayout(import, "running B point import");
                Check(import.Result.IsValid && import.Result.Added == 1, "valid CSV preview was not accepted.");
                SaveEditorScreenshot(import, "tag-import", "preview");
                await import.ApplyAndAcceptForSmokeAsync();
            }
            await WaitFor(() => _application.GetLatest().Any(sample => sample.DeviceId == b && sample.TagName == "仅本地采集" && sample.Quality == SampleQuality.Good), "imported point did not collect after target restart.");
            Check(!_configuration.Devices.Single(device => device.Id == b).Tags.Single(tag => tag.Name == "仅本地采集").UploadEnabled, "point upload selection was lost.");
            await AssertContinues(a, stamp, "point import into B interrupted A.");
            await AssertSavedConfigurationForSmokeAsync("live point import");

            var imported = Clone(fixture); imported.Devices = [new DeviceConfiguration
            {
                Name = "在线追加导入 D", DriverId = "simulator", PollIntervalMs = 100,
                Tags = [new TagConfiguration { Name = "导入点", Address = "sine", DataType = TagDataType.Float64 }]
            }];
            stamp = Latest(a);
            await AppendImportedDevicesAsync(imported);
            Check(!_application.IsDeviceRunning(imported.Devices[0].Id), "import unexpectedly started a new device.");
            await AssertContinues(a, stamp, "device import interrupted A.");

            ShowPage("cloud");
            ((TextBox)_cloudFields["name"]).Text = _configuration.Name + " 未应用草稿";
            Check(_dirty && _cloudFields["name"].Enabled && _cloudReset.Enabled && !_cloudApply.Enabled,
                "running devices blocked draft editing/reset or allowed global application.");
            await DeviceRowActionAsync(b, DeviceStopColumnName);
            await DeviceRowActionAsync(b, DeviceStartColumnName);
            Check(_application.IsDeviceRunning(b) && _dirty, "unapplied platform draft still blocks starting B.");
            var rejected = false;
            try { await ApplyCloudSettingsAsync(); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && _application.IsDeviceRunning(a) && _application.IsDeviceRunning(b), "global settings were applied while devices ran.");
            ResetCloudDraft(); BindCloudFields();

            stamp = Latest(a);
            await SmokeDeviceConfigurationModalAsync(() => ConfigureDeviceAsync(b), editor => editor.SetFieldForSmoke("Enabled", "false"), "disable running B");
            Check(!_application.IsDeviceRunning(b) && !_configuration.Devices.Single(device => device.Id == b).Enabled,
                "disabling B did not stop only B.");
            await AssertContinues(a, stamp, "disabling B interrupted A.");
            var removal = Clone(_configuration); removal.Devices.RemoveAll(device => device.Id == b);
            await CommitConfigurationAsync(removal, a);
            Check(_application.GetDeviceStatuses().All(status => status.DeviceId != b) && _application.GetLatest().All(sample => sample.DeviceId != b), "removed device left live rows behind.");
            await AssertSavedConfigurationForSmokeAsync("device removal while A runs");
            ShowPage("devices"); await RefreshSnapshotsAsync(); SaveEditorScreenshot(this, "live-configuration", "devices");
            ShowPage("diagnostics"); await RefreshSnapshotsAsync();
            Check(_telemetryDiagnosticsGrid.Rows.Count >= 12, "telemetry diagnostic snapshot was not displayed.");
            SaveEditorScreenshot(this, "live-configuration", "diagnostics");
            Console.WriteLine("Live configuration smoke passed: A kept collecting during C add, stopped/running B edit, modal save failure/retry, B CSV import, D import, platform-draft start, B disable/delete; only simulators ran and MQTT remained disabled.");
        }
        finally
        {
            await _application.StopAsync();
            await RestoreConfigurationForSmokeAsync(original);
            RebindConfigurationViews(originalSelection); ShowPage(originalPage); UpdateCommands();
        }
    }

    private async Task SmokeRetainedDeviceInputAsync(Guid id)
    {
        var original = Clone(_configuration);
        var disk = DeviceImportSmokeJson(await _application.ReadSavedConfigurationAsync());
        var draftName = "保存失败后保留并重试 B";
        Exception? failure = null;
        var observed = false;
        IDisposable? rejectedWrites = RejectConfigurationWritesForSmoke();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        var elapsed = Stopwatch.StartNew();
        timer.Tick += async (_, _) =>
        {
            var editor = System.Windows.Forms.Application.OpenForms.OfType<DeviceConfigurationDialog>().SingleOrDefault(form => form.Visible && form.Modal);
            if (editor is null)
            {
                if (elapsed.Elapsed < TimeSpan.FromSeconds(8)) return;
                timer.Stop(); failure = new InvalidOperationException("Failed-save editor did not open.");
                return;
            }
            timer.Stop(); observed = true;
            try
            {
                editor.SetFieldForSmoke("Name", draftName);
                var rejected = false;
                try { await editor.ApplyAndAcceptForSmokeAsync(); }
                catch (Exception exception) when (IsConfigurationWriteFailureForSmoke(exception)) { rejected = true; }
                var input = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "DeviceConfiguration_Name");
                if (!rejected || !editor.Visible || input.Text != draftName || !_application.IsDeviceRunning(id)
                    || DeviceImportSmokeJson(_configuration) != DeviceImportSmokeJson(original))
                    throw new InvalidOperationException("Failed save closed the editor, lost draft inputs, or changed a live device.");
                rejectedWrites.Dispose(); rejectedWrites = null;
                if (disk != DeviceImportSmokeJson(await _application.ReadSavedConfigurationAsync())) throw new InvalidOperationException("Failed live save modified the persisted SQLite configuration.");
                SaveEditorScreenshot(editor, "live-configuration", "retained-draft");
                await editor.ApplyAndAcceptForSmokeAsync();
                if (_configuration.Devices.Single(device => device.Id == id).Name != draftName)
                    throw new InvalidOperationException("Retry did not save the retained input.");
            }
            catch (Exception exception)
            {
                failure = exception;
                editor.DialogResult = DialogResult.Cancel; editor.Close();
            }
        };
        timer.Start();
        try { await ConfigureDeviceAsync(id); }
        finally { timer.Stop(); rejectedWrites?.Dispose(); }
        if (!observed || failure is not null) throw new InvalidOperationException("Retained-input smoke failed.", failure);
    }
}
