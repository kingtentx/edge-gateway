namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private IReadOnlyList<MenuCommand> BuildDeviceExchangeMenu()
    {
        var id = SelectedDevice()?.Id;
        bool Target() => CanEdit() && id.HasValue && _configuration.Devices.Any(device => device.Id == id);
        return [
            new("ImportDeviceExcel", "导入设备 Excel…", ImportDeviceWorkbookAsync, CanEdit),
            new("ExportSelectedDeviceExcel", "导出所选设备 Excel…", () => RunBusyAsync(() => ExportDeviceWorkbookAsync(id)), Target, "请先选择设备"),
            new("ExportAllDevicesExcel", "导出全部设备 Excel…", () => RunBusyAsync(() => ExportDeviceWorkbookAsync()),
                () => CanEdit() && _configuration.Devices.Count > 0, "尚无设备"),
            new("DeviceExcelTemplate", "下载设备 Excel 模板…", () => RunBusyAsync(() => ExportDeviceWorkbookAsync(template: true)), CanEdit)
        ];
    }

    private async Task ImportDeviceWorkbookAsync()
    {
        if (!CanEdit()) return;
        using var file = new OpenFileDialog { Filter = "设备 Excel 工作簿 (*.xlsx)|*.xlsx", Title = "导入设备及其点位", CheckFileExists = true };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        EdgeGateway.Model.DeviceConfigurationWorkbook workbook;
        _busy = true; UpdateCommands();
        try { workbook = await _application.ReadDeviceWorkbookAsync(file.FileName); }
        finally { _busy = false; UpdateCommands(); }
        using var editor = new DeviceWorkbookImportDialog(update => _application.PreviewDeviceWorkbook(workbook, update));
        BindEditorApply(editor, workbook.Devices.FirstOrDefault()?.Id, async () =>
        {
            var result = await _application.ImportDeviceWorkbookAsync(workbook, editor.UpdateExisting);
            CompleteConfigurationCommit(result, workbook.Devices.FirstOrDefault()?.Id,
                $"已导入并保存 {workbook.Devices.Count} 台设备及关联点位、通道和驱动参数。");
        });
        editor.ShowDialog(this);
    }

    private async Task ExportDeviceWorkbookAsync(Guid? deviceId = null, bool template = false)
    {
        using var file = new SaveFileDialog { Filter = "设备 Excel 工作簿 (*.xlsx)|*.xlsx", DefaultExt = "xlsx", AddExtension = true,
            FileName = template ? "设备导入模板.xlsx" : deviceId.HasValue ? "所选设备及点位.xlsx" : "全部设备及点位.xlsx",
            Title = template ? "保存设备模板" : "导出设备及其点位", OverwritePrompt = true };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        await _application.ExportDeviceWorkbookAsync(file.FileName, deviceId, template);
        SetFooter(template ? "设备 Excel 模板已导出，请修改示例行后导入。" : "设备 Excel 已导出，包含关联通道、驱动参数及全部点位。");
    }
}
