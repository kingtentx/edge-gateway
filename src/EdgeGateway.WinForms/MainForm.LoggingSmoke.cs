using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeLoggingAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("Logging smoke requires an isolated host.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(_smokeScreenshotPath ?? "ui-smoke.png"))!;
        Directory.CreateDirectory(directory);
        using var focusDiagnostics = StartSmokeFocusDiagnostics(directory);
        CheckUsability(_logTabs.TabPages.Cast<TabPage>().Select(tab => tab.Text).SequenceEqual(new[] { "日志设置", "运行日志", "采样数据" }), "Logging tab order is wrong.");
        var device = new DeviceConfiguration { Name = "日志验证设备", DriverId = "simulator", PollIntervalMs = 100,
            Tags = [new() { Name = "温度", Address = "constant:12.5", DataType = TagDataType.Float64 }] };
        CompleteConfigurationCommit(await _application.AddDeviceAsync(device), device.Id, "日志验证");
        await _application.StartDeviceAsync(device.Id);
        try
        {
            ShowPage("logs"); _logTabs.SelectedIndex = 0;
            ((ComboBox)_loggingFields["MinimumLevel"]).SelectedItem = GatewayLogLevel.Error;
            ((NumericUpDown)_loggingFields["SampleMaxFileSizeMb"]).Value = 3;
            ((NumericUpDown)_loggingFields["SampleRetainedFileCount"]).Value = 7;
            ((NumericUpDown)_loggingFields["SampleRetainedDays"]).Value = 8;
            using (RejectConfigurationWritesForSmoke())
            {
                var failed = false;
                try { await ApplyLoggingSettingsAsync(); } catch (Exception exception) when (IsConfigurationWriteFailureForSmoke(exception)) { failed = true; }
                CheckUsability(failed && _loggingDirty && CaptureLoggingSettings().SampleMaxFileSizeMb == 3,
                    "Failed logging save lost the draft.");
            }
            await ApplyLoggingSettingsAsync();
            CheckUsability(_application.IsDeviceRunning(device.Id) && (await _application.ReadSavedConfigurationAsync()).Logging.SampleRetainedDays == 8,
                "Logging hot apply stopped acquisition or lost sample policy.");
            _log.Write(GatewayLogLevel.Error, "日志分离验证：系统事件");
            // Await real simulator samples and the asynchronous file writer, never use a fake successful row.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(100);
                await _sampleArchive.QueryAsync();
                if (_sampleArchive.Result?.Entries.Count > 0) break;
            }
            CheckUsability(_sampleArchive.Result?.Entries.Count > 0 && _sampleArchive.Result.Entries.All(entry => entry.Kind == "sample" && entry.Sample is not null),
                "Sample query is empty at Error event level or contains events.");
            await _eventArchive.QueryAsync();
            CheckUsability(_eventArchive.Result?.Entries.Any(entry => entry.Message.Contains("日志分离验证")) == true
                && _eventArchive.Result.Entries.All(entry => entry.Kind == "event"), "Event query contains samples or lost event.");
            var original = _sampleArchive.Result;
            _sampleArchive.Keyword.Text = "未执行的筛选";
            CheckUsability(ReferenceEquals(original, _sampleArchive.Result) && _sampleArchive.State.Text.Contains("需重新查询"), "Draft filters replaced executed results.");
            await _application.ExportLogQueryAsync(original!, Path.Combine(directory, "samples.csv"));
            await _application.ExportLogQueryAsync(_eventArchive.Result!, Path.Combine(directory, "events.jsonl"));
            _sampleArchive.Keyword.Text = "";
            _sampleArchive.Device.Text = device.Name; _sampleArchive.Tag.Text = "温度";
            await _sampleArchive.QueryAsync();
            CheckUsability(_sampleArchive.Result!.Entries.Count > 0 && _sampleArchive.Result.Entries.All(entry => entry.Sample!.DeviceId == device.Id), "Sample device filter failed.");
            foreach (var (name, size) in new[] { ("wide", new Size(1200, 800)), ("narrow", new Size(740, 560)) })
            {
                var area = new Rectangle(Screen.FromControl(this).WorkingArea.Location, new Size(UiTheme.Scale(this, size.Width), UiTheme.Scale(this, size.Height)));
                MinimumSize = Size.Empty; MaximumSize = area.Size;
                if (!SetWindowPos(Handle, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, 0x0014))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                FitToWorkingArea(testWorkArea: area); await Task.Yield(); PerformLayoutRecursively(this);
                _logTabs.SelectedIndex = 0;
                foreach (var input in _loggingFields.Values) BringIntoViewAndAssert(input, "logging settings " + name + " " + input.Name);
                BringIntoViewAndAssert(_loggingApply, "logging apply " + name);
                ResetScrollPositions(_pages["logs"], navigationMenu);
                SaveSmokeScreenshot(this, Path.Combine(directory, $"logging-{name}-settings.png"));
                foreach (var view in new[] { _eventArchive, _sampleArchive })
                {
                    _logTabs.SelectedTab = view.Page; PerformLayoutRecursively(this);
                    foreach (var input in view.Inputs) BringIntoViewAndAssert(input, name + " " + input.Name);
                    BringIntoViewAndAssert(view.Grid, "logging results " + name);
                    CheckUsability(view.Grid.Rows.Count > 0 && view.Export.Enabled, "Query results or export are inaccessible.");
                    SaveSmokeScreenshot(this, Path.Combine(directory, $"logging-{name}-{(view == _sampleArchive ? "samples" : "events")}.png"));
                }
            }
            Console.WriteLine($"Separated logging smoke passed: settings/events/samples tabs, hot apply and failed-save draft, independent filtered results and exports, wide/narrow inputs at actual {DeviceDpi} DPI; isolated simulator only.");
        }
        finally { await _application.StopAsync(); }
    }
}
