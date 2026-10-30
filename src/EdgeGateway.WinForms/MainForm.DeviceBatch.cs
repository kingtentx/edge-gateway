using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly HashSet<Guid> _checkedDeviceIds = [];
    private Guid[] _filteredDeviceIds = [];

    private bool _syncingDeviceHeaderCheck;
    private void UpdateDeviceCheckSummary()
    {
        _deviceSummary.Text = $"{_filteredDeviceIds.Length} / {_configuration.Devices.Count} 台设备 · 已勾选 {_checkedDeviceIds.Count} 台";
        SyncDeviceHeaderCheck();
    }
    private void SyncDeviceHeaderCheck()
    {
        if (_devicesPageView is null) return;
        _syncingDeviceHeaderCheck = true;
        try
        {
            var count = _filteredDeviceIds.Count(_checkedDeviceIds.Contains);
            _devicesPageView.deviceHeaderCheck.CheckState = count == 0 ? CheckState.Unchecked
                : count == _filteredDeviceIds.Length ? CheckState.Checked : CheckState.Indeterminate;
            _devicesPageView.deviceHeaderCheck.Enabled = CanEdit() && _filteredDeviceIds.Length > 0;
        }
        finally { _syncingDeviceHeaderCheck = false; }
    }

    private IReadOnlyList<MenuCommand> BuildDeviceBatchMenu()
    {
        // Capture identities when the menu opens, independently of the highlighted row.
        var ids = _filteredDeviceIds.Where(_checkedDeviceIds.Contains).ToArray();
        bool Targets() => CanEdit() && ids.Length > 0;
        return [
            new("BatchStartDevices", "批量启动", () => RunDeviceBatchAsync(ids, DeviceBatchAction.Start), Targets, ""),
            new("BatchStopDevices", "批量停止", () => RunDeviceBatchAsync(ids, DeviceBatchAction.Stop), Targets, ""),
            new("BatchEnableDevices", "批量启用", () => RunDeviceBatchAsync(ids, DeviceBatchAction.Enable), Targets, "", SeparateBefore: true),
            new("BatchDisableDevices", "批量禁用", () => RunDeviceBatchAsync(ids, DeviceBatchAction.Disable), Targets, ""),
            new("BatchCopyDevices", "批量复制", () => RunDeviceBatchAsync(ids, DeviceBatchAction.Copy), Targets, "", SeparateBefore: true),
            new("BatchDeleteDevices", "批量删除…", () => DeleteCheckedDevicesAsync(ids), Targets, "", Dangerous: true)
        ];
    }

    private async Task DeleteCheckedDevicesAsync(Guid[] ids)
    {
        if (!CanEdit() || ids.Length == 0) return;
        var devices = ids.Distinct().Select(id => _configuration.Devices.FirstOrDefault(d => d.Id == id)
            ?? throw new InvalidOperationException("勾选设备已不存在，请刷新列表。")).ToArray();
        if (DatabasePointReferenceWarning(devices.SelectMany(d => d.Tags)) is { } warning) { ShowError(warning); return; }
        var names = string.Join("、", devices.Take(8).Select(d => d.Name)) + (devices.Length > 8 ? "等" : "");
        if (MessageBox.Show(this, $"删除勾选的 {devices.Length} 台设备及其 {devices.Sum(d => d.Tags.Count)} 个配置点位？\n{names}\n删除成功后停止对应设备，其他设备继续采集；已有外部数据和日志保留。",
            "批量删除设备", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunBusyAsync(async () => CompleteConfigurationCommit(await _application.DeleteDevicesAsync(ids),
            successMessage: $"已删除 {devices.Length} 台勾选设备及其点位。"));
    }

    private enum DeviceBatchAction { Start, Stop, Enable, Disable, Copy }

    private Task RunDeviceBatchAsync(Guid[] ids, DeviceBatchAction action) => RunBusyAsync(async () =>
    {
        if (ids.Length == 0) return;
        if (action == DeviceBatchAction.Copy)
        {
            var result = await _application.CopyDevicesAsync(ids);
            CompleteConfigurationCommit(result, successMessage: $"已复制 {result.Added.Count} 台设备及其点位；副本默认停用，请配置后再启用。");
            return;
        }
        if (action is DeviceBatchAction.Enable or DeviceBatchAction.Disable)
        {
            var enabled = action == DeviceBatchAction.Enable;
            var applied = await _application.SetDevicesEnabledAsync(ids, enabled);
            CompleteConfigurationCommit(applied, successMessage:
                $"批量{(enabled ? "启用" : "禁用")}完成：勾选 {ids.Length} 台，变更 {applied.Updated.Count} 台。"
                + (enabled ? "已启用设备仍保持原有运行状态，可通过批量启动开始采集。" : ""));
            return;
        }

        var start = action == DeviceBatchAction.Start;
        var actionText = start ? "启动" : "停止";
        var succeeded = 0; var skipped = 0; var failures = new List<string>();
        var details = new List<string>();
        foreach (var id in ids.Distinct())
        {
            var device = _configuration.Devices.FirstOrDefault(d => d.Id == id);
            var name = device?.Name ?? id.ToString();
            try
            {
                if (device is null) throw new InvalidOperationException("设备已不存在");
                var running = _application.IsDeviceRunning(id);
                var reason = start && !device.Enabled ? "未启用" : running == start ? start ? "已启动" : "已停止" : null;
                if (reason is not null) { skipped++; details.Add($"{name}：跳过（{reason}）"); continue; }
                if (start) await _application.StartDeviceAsync(id);
                else await _application.StopDeviceAsync(id);
                succeeded++;
                details.Add($"{name}：{(start ? "已发起采集" : "已停止")}");
            }
            catch (Exception exception)
            {
                failures.Add($"{name}：{exception.Message}");
            }
        }
        var summary = $"批量{actionText}完成：成功 {succeeded} 台，跳过 {skipped} 台，失败 {failures.Count} 台。"
            + (start && succeeded > 0 ? "连接结果请查看采集状态。" : "");
        var report = summary + Environment.NewLine + string.Join(Environment.NewLine, details.Concat(failures));
        _log.Write(failures.Count > 0 ? GatewayLogLevel.Warning : GatewayLogLevel.Information, report);
        SetFooter(report);
        _footer.ForeColor = failures.Count > 0 ? UiTheme.Red : UiTheme.Muted;
        UpdateDeviceConfigurationCommands();
    });
}
