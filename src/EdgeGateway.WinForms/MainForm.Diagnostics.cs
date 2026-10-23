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
        return UiTheme.PageLayout("通信诊断", "检查采集与上报链路。发送确认按平台协议判断，最终业务入库仍需在平台核对。",
            UiTheme.Toolbar(UiTheme.Button("刷新状态", async (_, _) => await RefreshSnapshotsAsync()),
                UiTheme.Button("导出诊断", async (_, _) => await RunBusyAsync(ExportDiagnosticsAsync))), _telemetryDiagnosticsGrid);
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
            new("MQTT 连接", _runtime.IsMqttConnected ? "已连接" : "未连接", "保持连接并按配置订阅 RPC；以最近确认判断实际发送结果。"),
            new("接入平台 / 上报策略", _configuration.Mqtt.Platform + " / " + _configuration.ReportMode, "变化上报基于最终工程值；失败入队不会前移比较基准。"),
            new("最近发送确认", Time(status.LastBrokerAcknowledgedAt), _configuration.Mqtt.Platform == MqttPlatform.ThingsCloud
                ? "ThingsCloud 须同时收到 MQTT PUBACK 与 attributes/response 成功；不代表最终业务入库。"
                : "MQTT QoS 1 PUBACK 确认时间，不代表平台业务入库。"),
            new("累计发送确认", status.AcknowledgedMessages.ToString("N0"), "本进程完成发送确认的队列报文次数；重复投递仍可能发生，不是所有底层 PUBACK 的计数。"),
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
        var rpc = _runtime.GetRpcStatus();
        rows.AddRange([
            new("RPC 订阅", !rpc.Enabled ? "未启用" : rpc.Subscribed ? "已订阅" : "等待连接 / 订阅", "仅接受 read/write 单点命令，保留消息不执行。"),
            new("远程写入权限", rpc.AllowRemoteWrite ? "已开启" : "关闭", "还需目标设备运行、点位启用并具有写权限。"),
            new("RPC 接收 / 完成 / 拒绝", $"{rpc.ReceivedRequests} / {rpc.CompletedRequests} / {rpc.RejectedRequests}", "远程写入复用本地通信调度和读回核对。"),
            new("RPC 重放 / 丢弃", $"{rpc.ReplayedRequests} / {rpc.DroppedMessages}", "持久去重避免重复写入；过载、超限或保留请求不执行。"),
            new("最近 RPC", rpc.LastRequestId ?? "尚无记录", Time(rpc.LastRequestAt) + " · " + (rpc.LastResult ?? "无结果")),
            new("RPC 错误", rpc.Error ?? "无", "订阅失败、权限拒绝、无效参数和执行错误在此显示。")
        ]);
        var devices = _configuration.Devices.ToDictionary(d => d.Id);
        foreach (var device in _runtime.GetCommunicationStatistics())
        {
            var name = devices[device.DeviceId].Name;
            rows.Add(new(name + " · 通信批次", $"读 {device.ReadRequests} / 写 {device.WriteRequests} / 失败 {device.FailedRequests}", "读取按驱动调用批次计数；不是底层协议帧或字节计数。"));
            rows.Add(new(name + " · 最近耗时", device.LastDurationMs.ToString("F1") + " ms", Time(device.LastRequestAt) + "；不含通道排队等待。"));
            rows.Add(new(name + " · 故障降速", device.DemotedUntil is null ? "未降速" : "下一次重试 " + Time(device.DemotedUntil), device.LastError ?? "当前无通信错误"));
        }
        _telemetryDiagnosticsGrid.DataSource = rows;
        foreach (DataGridViewRow row in _telemetryDiagnosticsGrid.Rows)
            if (row.DataBoundItem is DiagnosticRow data) row.Cells["Value"].ToolTipText = data.Value;
    }

    private async Task ExportDiagnosticsAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "诊断快照 (*.json)|*.json", FileName = "edge-gateway-diagnostics.json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var snapshot = new
        {
            capturedAt = DateTimeOffset.UtcNow, gatewayId = _configuration.GatewayId,
            telemetry = _runtime.GetTelemetryStatus(), rpc = _runtime.GetRpcStatus(),
            devices = _runtime.GetDeviceStatuses(), communication = _runtime.GetCommunicationStatistics()
        };
        await File.WriteAllTextAsync(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(snapshot,
            EdgeGateway.DataAccess.JsonConfigurationStore.SerializerOptions));
        SetFooter("通信与 RPC 诊断快照已导出。");
    }

    private sealed record DiagnosticRow(string Item, string Value, string Meaning);
}
