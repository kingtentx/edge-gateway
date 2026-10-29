using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool RowDensitySmoke { get; set; }
    internal bool DeviceColumnsSmoke { get; set; }

    private async Task SmokeRowDensityAsync()
    {
        if (DeviceColumnsSmoke) { await SmokeDeviceColumnsAsync(); return; }
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("Row density requires an isolated stopped host.");
        _refreshTimer.Stop();
        var original = Clone(_configuration);
        var originalBounds = Bounds;
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt.Enabled = false;
            fixture.Databases = []; fixture.DatabaseWriteGroups = []; fixture.Channels = [];
            fixture.Devices = new[] { "待启动设备", "运行中设备" }.Select(name => new DeviceConfiguration
            {
                Name = name, DriverId = "simulator", PollIntervalMs = 100,
                Tags = [new TagConfiguration { Name = "行高验证", Address = "constant:1", DataType = TagDataType.Float64 }]
            }).ToList();
            await CommitConfigurationAsync(fixture, fixture.Devices[0].Id, replaceCloudDraft: true);
            await DeviceRowActionAsync(fixture.Devices[1].Id, DeviceStartColumnName);
            await RefreshSnapshotsAsync();
            foreach (var scale in new[] { 100, 125, 150 })
            {
                var area = new Rectangle(Screen.FromControl(this).WorkingArea.Location,
                    new Size((int)Math.Round(1920d / scale * DeviceDpi / 96 * 100),
                        (int)Math.Round((1080d / scale * 100 - 48) * DeviceDpi / 96)));
                MinimumSize = Size.Empty; MaximumSize = area.Size;
                if (!SetWindowPos(Handle, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, 0x0014))
                    throw new System.ComponentModel.Win32Exception();
                FitToWorkingArea(testWorkArea: area);
                await Task.Yield();
                if (Bounds != area) throw new InvalidOperationException($"1080p equivalent bounds differ: {Bounds} != {area}.");
                foreach (var page in new[] { "devices", "realtime", "diagnostics" })
                {
                    ShowPage(page); await RefreshSnapshotsAsync(); await Task.Yield();
                    PerformLayoutRecursively(this);
                    var grid = page == "devices" ? _deviceGrid : page == "realtime" ? _realtimeGrid : _telemetryDiagnosticsGrid;
                    var header = grid.GetCellDisplayRectangle(0, -1, false).Height;
                    if (header < grid.Font.Height || header > UiTheme.Scale(grid, 40))
                        throw new InvalidOperationException($"Invalid compact header: {page} {header}.");
                    if (page != "diagnostics")
                        foreach (var row in grid.Rows)
                        {
                            var height = row.Height;
                            if (height < grid.Font.Height + UiTheme.Scale(grid, 4) || height > UiTheme.Scale(grid, 44))
                                throw new InvalidOperationException($"Invalid compact row: {page} {height}.");
                        }
                    if (page == "diagnostics" && scale == 150 && !grid.Rows.Any(row => row.Height > header))
                        throw new InvalidOperationException("Long diagnostic text did not expand its row at the narrow workspace.");
                    Console.WriteLine($"Row density: 1920x1080-{scale}pct equivalent; actual DPI={DeviceDpi}; {page}; header={header}; rows={string.Join(',', grid.Rows.Select(row => row.Height).Take(8))}.");
                    SaveEditorScreenshot(this, "row-density", page + "-" + scale);
                }
            }
            await DeviceRowActionAsync(fixture.Devices[1].Id, DeviceStopColumnName);
            if (_application.IsDeviceRunning(fixture.Devices[1].Id)) throw new InvalidOperationException("Row stop did not stop the simulator.");
            Console.WriteLine("Row density smoke passed: 1080p equivalent 100/125/150 percent workspaces; device buttons, realtime rows and wrapped diagnostics. Actual monitor DPI unchanged.");
        }
        finally
        {
            await _application.StopAsync();
            MaximumSize = Size.Empty; Bounds = originalBounds;
            await RestoreConfigurationForSmokeAsync(original);
        }
    }
}
