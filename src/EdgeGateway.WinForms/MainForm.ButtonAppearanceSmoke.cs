using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeButtonAppearanceAsync()
    {
        if (!_smokeTest || _application.IsRunning)
            throw new InvalidOperationException("ButtonAppearance requires an isolated stopped host.");
        _refreshTimer.Stop();
        SmokeAntdMigration();
        var original = Clone(_configuration);
        var originalSize = Size;
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false;
            fixture.Mqtt.Enabled = false;
            fixture.Devices = new[] { "待启动设备", "运行中设备" }.Select(name => new DeviceConfiguration
            {
                Name = name, DriverId = "simulator", PollIntervalMs = 100,
                Tags = [new TagConfiguration { Name = "模拟斜坡", Address = "ramp", DataType = TagDataType.Float64 }]
            }).ToList();
            await CommitConfigurationAsync(fixture, fixture.Devices[0].Id, replaceCloudDraft: true);
            ShowPage("devices");
            await DeviceRowActionAsync(fixture.Devices[1].Id, DeviceStartColumnName);
            await RefreshSnapshotsAsync();
            if (!_application.IsDeviceRunning(fixture.Devices[1].Id) || _application.IsDeviceRunning(fixture.Devices[0].Id))
                throw new InvalidOperationException("Row start targeted the wrong device.");
            await SmokeDefaultButtonStatesAsync();
            await SmokeCommandMenusAsync();
            foreach (var narrow in new[] { false, true })
            {
                Size = narrow ? new Size(MinimumSize.Width, originalSize.Height) : originalSize;
                foreach (var key in _pages.Keys)
                {
                    ShowPage(key);
                    await Task.Yield();
                    PerformLayoutRecursively(this);
                    foreach (var button in Descendants(_pages[key]).OfType<GatewayButton>())
                        if (button.BorderWidth != 1) throw new InvalidOperationException("Missing common button outline: " + key + "/" + button.Name);
                    AssertPageToolbarsFit(_pages[key], "button appearance " + key);
                    foreach (var tabs in Descendants(_pages[key]).OfType<TabControl>().ToArray())
                    {
                        foreach (TabPage tab in tabs.TabPages)
                        {
                            tabs.SelectedTab = tab;
                            PerformLayoutRecursively(this);
                            AssertPageToolbarsFit(tab, "button appearance " + key + "/" + tab.Text);
                            if (key == "logs")
                            {
                                var title = Descendants(_pages[key]).Single(control => control.Name == "LoggingPageTitle");
                                BringIntoViewAndAssert(title, "logging title");
                                if (title.RectangleToScreen(title.ClientRectangle).Bottom > tabs.PointToScreen(Point.Empty).Y)
                                    throw new InvalidOperationException("Logging title overlaps its tabs.");
                            }
                            ResetScrollPositions(tab, navigationMenu);
                            SaveEditorScreenshot(this, "button-appearance", key + "-" + tab.Name + (narrow ? "-narrow" : "-wide"));
                        }
                        tabs.SelectedIndex = 0;
                    }
                    ResetScrollPositions(_pages[key], navigationMenu);
                    SaveEditorScreenshot(this, "button-appearance", key + (narrow ? "-narrow" : "-wide"));
                }
            }
            Size = originalSize;
            ShowPage("devices");
            await DeviceRowActionAsync(fixture.Devices[1].Id, DeviceStopColumnName);
            await RefreshSnapshotsAsync();
            if (_application.IsRunning) throw new InvalidOperationException("Row stop did not stop the simulator.");
            VerifyDeviceRowCommandsForSmoke("semantic row buttons stopped");
            SaveEditorScreenshot(this, "button-appearance", "devices-stopped");
            Console.WriteLine($"ButtonAppearance smoke passed: DeviceDpi={DeviceDpi}; eight pages and tabs, wide/narrow outlines and reachability, official hover/pressed/disabled states, keyboard, simulator row start/stop and logging title.");
        }
        finally
        {
            await _application.StopAsync();
            await RestoreConfigurationForSmokeAsync(original);
            Size = originalSize;
        }
    }
}
