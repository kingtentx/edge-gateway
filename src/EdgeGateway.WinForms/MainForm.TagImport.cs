using EdgeGateway.Model;
using EdgeGateway.Application;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task ImportTagsAsync(Guid? deviceId = null)
    {
        if (!CanEdit()) return;
        var device = deviceId.HasValue ? _configuration.Devices.FirstOrDefault(d => d.Id == deviceId) : PointDevice();
        if (device is null) { SetFooter("请先选择要导入点位的设备。"); return; }
        try
        {
            using var file = new OpenFileDialog { Filter = "点位 Excel (*.xlsx)|*.xlsx|点位 CSV (*.csv)|*.csv", Title = "导入当前设备点位", CheckFileExists = true };
            if (file.ShowDialog(PointDialogOwner) != DialogResult.OK) return;
            if (new FileInfo(file.FileName).Length > TagCsvExchange.MaxFileBytes)
                throw new InvalidDataException($"点位文件超过 {TagCsvExchange.MaxFileBytes / (1024 * 1024)} MB，请拆分后导入。");
            byte[] bytes;
            _busy = true; UpdateCommands();
            try { bytes = string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase)
                ? await _application.ReadTagWorkbookAsync(file.FileName) : await File.ReadAllBytesAsync(file.FileName); }
            finally { _busy = false; UpdateCommands(); }
            using var editor = CreateTagImportDialog(device, bytes);
            editor.ShowDialog(PointDialogOwner);
        }
        catch (Exception exception)
        {
            if (_smokeTest) throw;
            ShowError(exception.Message);
        }
    }

    private TagImportDialog CreateTagImportDialog(DeviceConfiguration device, byte[] bytes)
    {
        GatewayConfiguration Candidate(IReadOnlyList<TagConfiguration> tags)
        {
            var candidate = Clone(_configuration);
            var target = candidate.Devices.FirstOrDefault(x => x.Id == device.Id)
                ?? throw new InvalidOperationException("导入目标设备已不存在。");
            target.Tags = Clone(tags.ToList());
            return candidate;
        }
        var editor = new TagImportDialog(device.Name, bytes, Clone(device.Tags),
            tags => ConfigurationValidator.Validate(Candidate(tags), _drivers));
        BindEditorApply(editor, device.Id, async () =>
        {
            var result = editor.Result;
            if (!result.IsValid) throw new InvalidDataException("请先修正点位文件中的错误。");
            var applied = await _application.ImportTagFileAsync(device.Id, bytes, editor.Mode);
            CompleteConfigurationCommit(applied, device.Id,
                $"点位已导入并自动保存：新增 {result.Added} 个，更新 {result.Updated} 个，共 {result.TotalCount} 个。");
        });
        return editor;
    }

    private async Task ExportTagsAsync(bool template = false, Guid? deviceId = null)
    {
        var device = deviceId.HasValue ? _configuration.Devices.FirstOrDefault(d => d.Id == deviceId) : PointDevice();
        if (!template && device is null) { SetFooter("请先选择要导出点位的设备。"); return; }
        using var file = new SaveFileDialog
        {
            Filter = "点位 Excel (*.xlsx)|*.xlsx|点位 CSV (*.csv)|*.csv", DefaultExt = "xlsx", AddExtension = true,
            FileName = template ? "点位导入模板.xlsx" : "设备点位.xlsx",
            Title = template ? "保存点位模板" : "导出当前设备点位", OverwritePrompt = true
        };
        if (file.ShowDialog(PointDialogOwner) != DialogResult.OK) return;
        if (file.FilterIndex == 2)
            await File.WriteAllBytesAsync(file.FileName, TagCsvExchange.Export(template ? [] : device!.Tags));
        else
            await _application.ExportTagWorkbookAsync(file.FileName, device?.Id, template);
        SetFooter(template ? "点位模板已导出；请修改示例行，按列填写后导入到选中的设备。" : $"已导出 {device!.Tags.Count} 个点位，包含协议、换算、报警和上传设置。");
    }
}
