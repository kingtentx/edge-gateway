using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeWorkbooksAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("Workbook smoke requires isolated, stopped configuration.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(_smokeScreenshotPath ?? "ui-smoke.png"))!;
        Directory.CreateDirectory(directory);
        var device = new DeviceConfiguration { Name = "Excel 导入检查", DriverId = "simulator",
            Tags = [new() { Name = "温度", Address = "constant:12.5", DataType = TagDataType.Float64 }] };
        var applied = await _application.AddDeviceAsync(device);
        CompleteConfigurationCommit(applied, device.Id, "Excel 检查");
        var path = Path.Combine(directory, "设备样例.xlsx");
        await _application.ExportDeviceWorkbookAsync(path, device.Id);
        var workbook = await _application.ReadDeviceWorkbookAsync(path);
        workbook.Devices[0].Id = Guid.NewGuid();
        workbook.Devices[0].Name = "Excel 预览设备";
        workbook.Devices[0].Tags[0].Id = Guid.NewGuid();
        var pointPath = Path.Combine(directory, "点位样例.xlsx");
        await _application.ExportTagWorkbookAsync(pointPath, device.Id);
        var bytes = await _application.ReadTagWorkbookAsync(pointPath);
        ShowPage("devices");
        foreach (var (name, size) in new[] { ("wide", new Size(1200, 800)), ("narrow", new Size(740, 560)) })
        {
            var area = new Rectangle(Screen.FromControl(this).WorkingArea.Location,
                new Size(UiTheme.Scale(this, size.Width), UiTheme.Scale(this, size.Height)));
            MinimumSize = Size.Empty; MaximumSize = area.Size;
            if (!SetWindowPos(Handle, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, 0x0014))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            FitToWorkingArea(testWorkArea: area);
            await Task.Yield(); PerformLayoutRecursively(this);
            BringIntoViewAndAssert(_deviceExchange, "Excel device menu " + name);
            OpenMenuForSmoke(_deviceExchange, "Excel device menu " + name);
            CheckUsability(MenuItem(_deviceExchange, "ImportDeviceExcel").Enabled
                && MenuItem(_deviceExchange, "ExportSelectedDeviceExcel").Enabled
                && MenuItem(_deviceExchange, "ExportAllDevicesExcel").Enabled
                && MenuItem(_deviceExchange, "DeviceExcelTemplate").Enabled, "Excel device actions missing.");
            _deviceExchange.OpenedMenu.Close();
            _pointsDialog.Show(this);
            OpenMenuForSmoke(_tagMore, "Excel point menu " + name);
            CheckUsability(MenuItem(_tagMore, "ImportTags").Text?.Contains("Excel") == true
                && MenuItem(_tagMore, "ExportTags").Enabled, "Excel point actions missing.");
            _tagMore.OpenedMenu.Close();
            _pointsDialog.Hide();
            SaveSmokeScreenshot(this, Path.Combine(directory, $"workbook-{name}-devices.png"));
            using (var editor = new DeviceWorkbookImportDialog(update => _application.PreviewDeviceWorkbook(workbook, update)))
            {
                editor.Show(this);
                FitSmokeDialog(editor, area);
                VerifyEditorLayout(editor, "Excel device import " + name);
                var errors = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "DeviceWorkbookErrors");
                CheckUsability(errors.Text.StartsWith("校验通过"), "Valid Excel workbook preview failed.");
                BringIntoViewAndAssert(errors, "Excel device preview errors");
                SaveSmokeScreenshot(editor, Path.Combine(directory, $"workbook-{name}-device-preview.png"));
                editor.Close();
            }
            using (var editor = CreateTagImportDialog(device, bytes))
            {
                editor.SelectUpdateModeForSmoke();
                editor.Show(this);
                FitSmokeDialog(editor, area);
                VerifyEditorLayout(editor, "Excel point import " + name);
                CheckUsability(editor.Result.IsValid && editor.Result.Tags[0].Id == device.Tags[0].Id, "Excel point import lost stable identity.");
                SaveSmokeScreenshot(editor, Path.Combine(directory, $"workbook-{name}-point-preview.png"));
                editor.Close();
            }
        }
        Console.WriteLine("Excel workbook smoke passed: real workbook files, device and point menus, both import previews, wide/narrow reachability, six screenshots. Host DPI only; no business database connection.");
    }
}
