using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeDeviceColumnsAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("Device columns require an isolated stopped host.");
        _refreshTimer.Stop();
        var original = Clone(_configuration);
        var originalBounds = Bounds;
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt.Enabled = false;
            fixture.Databases = []; fixture.DatabaseWriteGroups = []; fixture.Channels = [];
            fixture.Devices = new[] { "演示机组", "设备列间距检查：较长的设备名称", "设备示例 2", "设备示例 3" }.Select(name => new DeviceConfiguration
            {
                Name = name, DriverId = "simulator", PollIntervalMs = 1000,
                Tags = [new TagConfiguration { Name = "温度", Address = "constant:1", DataType = TagDataType.Float64 }]
            }).ToList();
            await CommitConfigurationAsync(fixture, fixture.Devices[0].Id, replaceCloudDraft: true);
            ShowPage("devices");
            var grid = _deviceGrid;
            foreach (var width in new[] { 2200, 1600, 1000, 1600 })
            {
                var area = new Rectangle(Screen.FromControl(this).WorkingArea.Location,
                    new Size(UiTheme.Scale(this, width), UiTheme.Scale(this, 800)));
                MinimumSize = Size.Empty; MaximumSize = area.Size;
                if (!SetWindowPos(Handle, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, 0x0014))
                    throw new System.ComponentModel.Win32Exception();
                FitToWorkingArea(testWorkArea: area);
                await RefreshSnapshotsAsync(); await Task.Delay(150); PerformLayoutRecursively(this);
                grid.HorizontalScrollingOffset = 0;
                using var headerFont = new Font(grid.Font, FontStyle.Bold);
                foreach (var column in grid.Columns.Where(column => column.Visible))
                {
                    var textWidth = TextRenderer.MeasureText(column.HeaderText, headerFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                    Require(column.WidthPixel >= textWidth + UiTheme.Scale(grid, 28),
                        $"Header lacks spacing: {column.Name}; width={column.WidthPixel}; text={textWidth}.");
                }
                foreach (var row in grid.Rows)
                {
                    Require(row.Height <= UiTheme.Scale(grid, 44), "Device rows became too tall.");
                    foreach (var name in new[] { DeviceStartColumnName, DeviceStopColumnName, "DeviceConfiguration", "DevicePoints" })
                    {
                        var cell = row.Cells[name];
                        var content = cell.GetContentBounds(row.Index);
                        Require(content.Width > 0 && content.Left >= UiTheme.Scale(grid, 8)
                            && content.Right <= cell.OwningColumn.WidthPixel - UiTheme.Scale(grid, 8),
                            $"Action lacks side spacing: {name}; content={content}; column={cell.OwningColumn.WidthPixel}.");
                    }
                }
                SaveEditorScreenshot(this, "device-columns", width + "-left");
                if (width == 1000)
                {
                    var last = grid.Columns.Last(column => column.Visible);
                    grid.FirstDisplayedScrollingColumnIndex = last.Index;
                    Require(grid.HorizontalScrollingOffset > 0 && grid.GetCellDisplayRectangle(last.Index, 0, true).Width > 0,
                        "Cannot reach trailing device columns.");
                    SaveEditorScreenshot(this, "device-columns", "1000-right");
                }
                Console.WriteLine($"Device columns: logical window={width}; DeviceDpi={DeviceDpi}; header={grid.ColumnHeadersHeight}; rows={string.Join(',', grid.Rows.Select(row => row.Height))}.");
            }
            grid.HorizontalScrollingOffset = 0;
            grid.CurrentCell = grid.Rows[1].Cells["Name"];
            VerifyTableColumnDrag(grid);
            var resizedColumn = grid.Columns.First(column => column.Visible && !column.Frozen);
            var dragged = resizedColumn.WidthPixel;
            var selection = grid.CurrentCell?.Value;
            await RefreshSnapshotsAsync(); await RefreshSnapshotsAsync();
            Require(resizedColumn.WidthPixel == dragged && Equals(grid.CurrentCell?.Value, selection), "Device refresh lost dragged width or selection.");
            SaveEditorScreenshot(this, "device-columns", "dragged-refresh");
            Console.WriteLine($"Device columns smoke passed: header/action spacing, compact rows, wide/narrow round trip, horizontal reachability and official drag/refresh preservation; DeviceDpi={DeviceDpi}.");
        }
        finally
        {
            MaximumSize = Size.Empty; Bounds = originalBounds;
            await RestoreConfigurationForSmokeAsync(original);
        }
    }
}
