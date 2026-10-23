using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly DataGridView _telemetryDiagnosticsGrid = UiTheme.Grid();

    private Control BuildDiagnosticsPage()
    {
        UiTheme.AddColumn(_telemetryDiagnosticsGrid, "Item", "诊断项目", 180, 180);
        UiTheme.AddColumn(_telemetryDiagnosticsGrid, "Value", "当前值 / 最近事件", 260, 260);
        UiTheme.AddColumn(_telemetryDiagnosticsGrid, "Meaning", "说明", 350, 350);
        return UiTheme.PageLayout("通信诊断", "检查本地采样与 MQTT 上报链路。Broker 确认表示消息已接收，业务平台是否入库仍需在平台核对。",
            UiTheme.Toolbar(UiTheme.Button("刷新状态", async (_, _) => await RefreshSnapshotsAsync())), _telemetryDiagnosticsGrid);
    }

    private void RefreshTelemetryDiagnostics(StoreStatistics statistics)
    {
        var status = _runtime.GetTelemetryStatus();
        static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无记录";
        var phase = !status.Enabled ? "上报未启用" : !status.Running ? "已停止" : status.Sending ? "正在发送"
            : status.SendError is not null ? "等待重试" : "等待待发送消息";
        var rows = new List<DiagnosticRow>
        {
            new("上报任务", phase, "使用已保存的设置；平台草稿不会影响当前任务。"),
            new("MQTT 连接", _runtime.IsMqttConnected ? "已连接" : "未连接", "有待发送消息时按需连接；以最近确认判断实际发送结果。"),
            new("最近 Broker 确认", Time(status.LastBrokerAcknowledgedAt), "QoS 1 确认时间，不代表平台业务入库。"),
            new("累计 Broker 确认", status.AcknowledgedMessages.ToString("N0"), "本进程累计确认次数；异常重试可能重复发送同一 messageId。"),
            new("待补传消息", statistics.OutboxCount.ToString("N0"), "已持久化消息按顺序发送，确认后才从队列移除。"),
            new("最早待补传时间", Time(statistics.OldestOutboxAt), "用于判断积压持续时间；已入队消息按原载荷继续补传。"),
            new("最近生成入队", Time(status.LastEnqueuedAt), "只包含启用设备中启用采集且开启上传的点位。"),
            new("最近发送尝试", Time(status.LastSendAttemptAt), "发送失败不会删除待补传记录。"),
            new("连续补传失败", status.ConsecutiveFailures.ToString(), "补传读取、发送或确认移除的连续失败；成功后归零，正常停止不计入失败。"),
            new("下次重试", Time(status.NextRetryAt), "失败按退避策略重试；设备采集独立继续。"),
            new("当前发送错误", status.SendError ?? "无", "恢复成功后清除；历史错误保留在运行日志。"),
            new("队列容量", status.QueueCapacityBlocked ? "已满，暂停生成快照" : "正常", "已排队数据保留；本地采集与历史记录继续。"),
            new("当前持久化错误", status.PersistenceError ?? "无", "包括快照入队或确认后移除失败，请检查数据目录和磁盘。"),
            new("最近发送失败", Time(status.LastSendFailureAt), "最近失败事件时间，恢复后仍保留用于排查。"),
            new("本地历史记录", statistics.HistoryCount.ToString("N0"), "上传开关不影响本地采集和历史保存。")
        };
        _telemetryDiagnosticsGrid.DataSource = rows;
        foreach (DataGridViewRow row in _telemetryDiagnosticsGrid.Rows)
            if (row.DataBoundItem is DiagnosticRow data) row.Cells["Value"].ToolTipText = data.Value;
    }

    private sealed record DiagnosticRow(string Item, string Value, string Meaning);
}
