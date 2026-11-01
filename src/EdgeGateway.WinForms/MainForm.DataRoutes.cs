using System.Text;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task ShowDataRoutesAsync(Guid? deviceId, Guid? tagId)
    {
        try { await ShowDataRoutesCoreAsync(deviceId, tagId); }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task ShowDataRoutesCoreAsync(Guid? deviceId, Guid? tagId)
    {
        var current = _application.GetConfiguration();
        var device = current.Devices.FirstOrDefault(item => item.Id == deviceId);
        if (device is null) { SetFooter("请先选择设备。"); return; }
        var tag = device.Tags.FirstOrDefault(item => item.Id == tagId);
        if (tagId.HasValue && tag is null) { SetFooter("点位已不存在，请刷新后重试。"); return; }
        var state = _application.GetDeviceStatuses().FirstOrDefault(item => item.DeviceId == device.Id);
        var text = new StringBuilder();
        text.AppendLine($"设备：{device.Name}（{device.Id}）");
        text.AppendLine($"允许采集：{(device.Enabled ? "是" : "否")}；当前状态：{StateText(state?.State ?? DeviceState.Stopped)}");
        text.AppendLine("允许采集是保存的配置；启动后才会采样。新设备保存后保持停止。");
        var ids = tag is null ? device.Tags.Select(item => item.Id).ToHashSet() : new HashSet<Guid> { tag.Id };
        if (tag is not null)
        {
            text.AppendLine($"\n点位：{tag.Name}（{tag.Id}）\n地址：{tag.Address}\n允许采集：{(tag.Enabled ? "是" : "否")}\n读写：{(tag.Writable ? "读 / 写" : "只读")}");
            var point = _realtimeDisplay.Update(current.Devices, _application.GetDeviceStatuses(), _application.GetLatest()).First(item => item.Sample.TagId == tag.Id);
            text.AppendLine(point.HasSample
                ? $"原始值：{ValueText(point.Sample.RawValue)}\n工程值：{ValueText(point.Sample.Value)} {point.Sample.Unit}\n采样时间：{point.Sample.Timestamp.ToLocalTime():O}"
                : "尚无采样；可先在设备或点位菜单执行连接与读点诊断。");
            text.AppendLine($"质量：{QualityText(point.Sample.Quality)}\n状态信息：{point.Sample.Error ?? "—"}");
        }
        text.AppendLine($"\n采样日志：{(current.Logging.LogAcquisitionSamples ? "已启用" : "未启用")}；历史查询可读取保留范围内的采样日志。");
        text.AppendLine($"平台上传：{device.Tags.Count(item => ids.Contains(item.Id) && device.Enabled && item.Enabled && item.UploadEnabled)} 个点位允许上传；还需平台已启用并取得发送确认。");
        var statuses = _application.GetDatabaseGroupStatuses().ToDictionary(item => item.GroupId);
        var routes = current.DatabaseWriteGroups.Where(group => group.Mode == DatabaseStorageMode.ColumnMapping
            ? group.Columns.Any(column => column.TagId is Guid id && ids.Contains(id))
            : DatabasePointSelection.Resolve(group, current.Devices).Overlaps(ids)).ToArray();
        text.AppendLine($"\n数据库配置路由：{routes.Length} 个写入组（按当前允许采集的点位解析）");
        foreach (var group in routes)
        {
            var connection = current.Databases.First(item => item.Id == group.ConnectionId);
            statuses.TryGetValue(group.Id, out var status);
            text.AppendLine($"\n{group.Name} → {connection.Name} / {new DatabaseTableInfo(group.Schema, group.Table)}");
            text.AppendLine($"{DatabaseUiText.Mode(group.Mode)}；{DatabaseUiText.Selection(group, current.Devices)}");
            text.AppendLine(status?.IsDraining == true ? "正在按原配置排空；新采样不进入此组。"
                : !group.Enabled || !connection.Enabled ? "已暂停；暂停期间新采样不自动补齐。"
                : status?.IsAccepting == true ? "正在接收采样；仍须满足映射的质量、时效和完整性要求。" : "等待采集启动。");
            text.AppendLine($"本组成功写入：{status?.WrittenRows ?? 0} 行；最近成功：{status?.LastSuccess?.ToLocalTime().ToString("O") ?? "尚无记录"}");
            text.AppendLine("本组状态：" + (status?.Error ?? "无已报告错误"));
            text.AppendLine("历史查询：" + (DatabaseHistoryMapping.GetUnavailableReason(group) ?? "映射支持按设备、点位和时间查询；仍需目标表可访问。"));
        }
        if (routes.Length == 0) text.AppendLine("可在数据库写入组使用“按设备 / 分组动态选点”，新增启用点位自动纳入；自定义整行需要逐字段明确绑定。");
        using var dialog = new DataRoutesDialog(device.Name + (tag is null ? " · 数据去向" : " / " + tag.Name), text.ToString());
        dialog.ShowDialog(PointDialogOwner);
        // A point-list modal owns its own message loop; close it before switching the main page.
        if (dialog.Destination is null) return;
        if (PointDialogOwner is Form owner && owner != this) owner.Close();
        switch (dialog.Destination)
        {
            case "realtime":
                _realtimeSearch.Clear(); _realtimePageView.anomaliesOnly.Checked = false;
                SelectRealtimeDevice(device.Id); ShowPage("realtime"); RefreshRealtime(_application.GetLatest()); break;
            case "history":
                _historySource.SelectedIndex = 0; SelectChoice(_historyDevice, device.Id); SelectChoice(_historyTag, tagId);
                ShowPage("history"); await RunBusyAsync(QueryHistoryAsync); break;
            case "logs":
                _sampleArchive.Device.Text = device.Id.ToString(); _sampleArchive.Tag.Text = tagId?.ToString() ?? "";
                _sampleArchive.Keyword.Clear(); _sampleArchive.Quality.SelectedIndex = 0;
                _sampleArchive.To.Value = DateTime.Now; _sampleArchive.From.Value = DateTime.Now.AddHours(-1);
                _logTabs.SelectedTab = _sampleArchive.Page; ShowPage("logs"); _sampleArchive.Search.PerformClick(); break;
            case "database":
                ShowPage("database"); _databaseTabs.SelectedIndex = 1;
                BindDatabaseViews(groupId: routes.FirstOrDefault()?.Id); break;
            case "diagnostics":
                _diagnosticDeviceId = device.Id; ShowPage("diagnostics");
                RefreshTelemetryDiagnostics(await _application.GetStatisticsAsync()); break;
        }
    }
}
