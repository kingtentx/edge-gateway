namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private string[] DeviceGroupNames() => _configuration.DeviceGroups.Concat(_configuration.Devices.Select(d => d.Group))
        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string UniqueCatalogName(string prefix, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = 1;
        while (names.Contains($"{prefix} {index}")) index++;
        return $"{prefix} {index}";
    }

    private void ShowConfigurationCatalog(bool groups)
    {
        if (!CanEdit()) return;
        using var dialog = new ConfigurationCatalogDialog();
        dialog.Bind(groups ? "分组管理" : "通道管理", () => groups
            ? DeviceGroupNames().Select(name => new ConfigurationCatalogRow(name, name,
                _configuration.Devices.Count(d => string.Equals(d.Group, name, StringComparison.OrdinalIgnoreCase)),
                "重命名同步更新设备；空分组会保留")).ToArray()
            : _configuration.Channels.Select(channel => new ConfigurationCatalogRow(channel.Id.ToString(), channel.Name,
                _configuration.Devices.Count(d => d.ChannelId == channel.Id),
                $"间隔 {channel.InterRequestDelayMs} ms · {channel.Description}")).ToArray(),
            () => CanEdit() && !_application.IsRunning,
            async target =>
            {
                if (!groups)
                {
                    var saved = await EditChannelAsync(target is null, target is null ? null : Guid.Parse(target.Key), dialog);
                    return saved?.ToString() ?? target?.Key;
                }
                using var editor = new DeviceGroupEditor(target?.Name ?? UniqueCatalogName("分组", DeviceGroupNames()));
                BindEditorApply(editor, null, async () =>
                {
                    var applied = await _application.SaveDeviceGroupAsync(target?.Key, editor.Value);
                    CompleteConfigurationCommit(applied, successMessage: "设备分组已自动保存。");
                });
                editor.ShowDialog(dialog);
                return editor.DialogResult == DialogResult.OK ? editor.Value : target?.Key;
            }, async target =>
            {
                var applied = groups ? await _application.DeleteDeviceGroupAsync(target.Key)
                    : await _application.DeleteChannelAsync(Guid.Parse(target.Key));
                CompleteConfigurationCommit(applied, successMessage: groups ? "设备分组已删除。" : "通信通道已删除。");
            });
        dialog.ShowDialog(this);
    }
}
