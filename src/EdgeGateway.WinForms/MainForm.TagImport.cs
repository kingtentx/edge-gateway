using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task ImportTagsAsync()
    {
        if (!CanEdit()) return;
        var device = SelectedDevice();
        if (device is null) { SetFooter("请先选择要导入点位的设备。"); return; }
        try
        {
            using var file = new OpenFileDialog { Filter = "点位 CSV (*.csv)|*.csv", Title = "导入当前设备点位", CheckFileExists = true };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            if (new FileInfo(file.FileName).Length > TagCsvExchange.MaxFileBytes)
                throw new InvalidDataException($"点位文件超过 {TagCsvExchange.MaxFileBytes / (1024 * 1024)} MB，请拆分后导入。");
            byte[] bytes;
            _busy = true; UpdateCommands();
            try { bytes = await File.ReadAllBytesAsync(file.FileName); }
            finally { _busy = false; UpdateCommands(); }
            using var editor = CreateTagImportDialog(device, bytes);
            editor.ShowDialog(this);
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
            await CommitConfigurationAsync(Candidate(result.Tags), device.Id,
                $"点位已导入并自动保存：新增 {result.Added} 个，更新 {result.Updated} 个，共 {result.TotalCount} 个。");
        });
        return editor;
    }

    private async Task ExportTagsAsync(bool template = false)
    {
        var device = SelectedDevice();
        if (!template && device is null) { SetFooter("请先选择要导出点位的设备。"); return; }
        using var file = new SaveFileDialog
        {
            Filter = "点位 CSV (*.csv)|*.csv", FileName = template ? "edge-tags-template.csv" : "edge-device-tags.csv",
            Title = template ? "保存点位模板" : "导出当前设备点位", OverwritePrompt = true
        };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        await File.WriteAllBytesAsync(file.FileName, TagCsvExchange.Export(template ? [] : device!.Tags));
        SetFooter(template ? "点位模板已导出；按列填写后导入到选中的设备。" : $"已导出 {device!.Tags.Count} 个点位，包含协议、换算、报警和上传设置。");
    }
}
