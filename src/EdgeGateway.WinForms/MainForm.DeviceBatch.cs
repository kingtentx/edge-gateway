using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly HashSet<Guid> _checkedDeviceIds = [];
    private Guid[] _filteredDeviceIds = [];

    private void UpdateDeviceCheckSummary() => _deviceSummary.Text =
        $"{_filteredDeviceIds.Length} / {_configuration.Devices.Count} 台设备 · 已勾选 {_checkedDeviceIds.Count} 台";

    private IReadOnlyList<MenuCommand> BuildDeviceBatchMenu()
    {
        // Capture identities when the menu opens, independently of the highlighted row.
        var ids = _filteredDeviceIds.Where(_checkedDeviceIds.Contains).ToArray();
        var filtered = _filteredDeviceIds.ToArray();
        bool Targets() => CanEdit() && ids.Length > 0;
        return [
            new("BatchStartDevices", $"批量启动（{ids.Length} 台）", () => RunDeviceBatchAsync(ids, DeviceBatchAction.Start), Targets, "请先勾选设备"),
            new("BatchStopDevices", $"批量停止（{ids.Length} 台）", () => RunDeviceBatchAsync(ids, DeviceBatchAction.Stop), Targets, "请先勾选设备"),
            new("BatchEnableDevices", "批量启用", () => RunDeviceBatchAsync(ids, DeviceBatchAction.Enable), Targets, "请先勾选设备", SeparateBefore: true),
            new("BatchDisableDevices", "批量禁用（停止对应设备）", () => RunDeviceBatchAsync(ids, DeviceBatchAction.Disable), Targets, "请先勾选设备"),
            new("CheckFilteredDevices", $"勾选筛选结果（{filtered.Length} 台，含全部页）", () =>
            {
                _checkedDeviceIds.UnionWith(filtered.Where(_filteredDeviceIds.Contains));
                UpdateDeviceCheckSummary(); _deviceGrid.RepaintRows(); return Task.CompletedTask;
            }, () => CanEdit() && filtered.Length > 0, SeparateBefore: true),
            new("ClearCheckedDevices", "清空勾选", () =>
            {
                _checkedDeviceIds.Clear(); UpdateDeviceCheckSummary(); _deviceGrid.RepaintRows(); return Task.CompletedTask;
            }, () => CanEdit() && _checkedDeviceIds.Count > 0)
        ];
    }

    private enum DeviceBatchAction { Start, Stop, Enable, Disable }

    private Task RunDeviceBatchAsync(Guid[] ids, DeviceBatchAction action) => RunBusyAsync(async () =>
    {
        if (ids.Length == 0) return;
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
        UpdateDeviceConfigurationCommands();
    });
}
