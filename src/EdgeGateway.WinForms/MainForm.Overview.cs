using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private Task? _serverResourceRefresh;
    private readonly Label _deviceCount = UiTheme.Label("0", 26, UiTheme.Ink, true);
    private readonly Label _onlineCount = UiTheme.Label("0", 26, UiTheme.Green, true);
    private readonly Label _abnormalCount = UiTheme.Label("0", 26, UiTheme.Red, true);
    private readonly Label _stoppedCount = UiTheme.Label("0", 26, UiTheme.Muted, true);
    private readonly Label _overviewDeviceSummary = UiTheme.Label("等待设备状态", 10, UiTheme.Muted);
    private readonly Label _healthState = UiTheme.Label("等待采集", 18, UiTheme.Muted, true);
    private readonly Label _onlineRate = UiTheme.Label("在线率 —", 10, UiTheme.Muted);
    private readonly Label _pointHealth = UiTheme.Label("等待采样状态", 10, UiTheme.Muted);
    private readonly Label _connectionHealth = UiTheme.Label("连接中 0 · 重连中 0 · 故障 0", 10, UiTheme.Muted);
    private readonly Label _pendingCount = UiTheme.Label("待补传 0 条", 14, UiTheme.Ink, true);
    private readonly Label _mqttStatus = UiTheme.Label("平台未启用", 18, UiTheme.Muted, true);
    private readonly Label _outputHealth = UiTheme.Label("等待输出状态", 10, UiTheme.Muted);
    private readonly Label _serverIdentity = UiTheme.Label("服务器资源", 12, UiTheme.Ink, true);
    private readonly Label _serverCpu = UiTheme.Label("采样中", 16, UiTheme.Blue, true);
    private readonly Label _serverMemory = UiTheme.Label("采样中", 16, UiTheme.Green, true);
    private readonly Label _serverCpuTitle = UiTheme.Label("CPU", 10, UiTheme.Ink, true);
    private readonly Label _serverMemoryTitle = UiTheme.Label("内存", 10, UiTheme.Ink, true);
    private readonly ServerUsageTrend _serverCpuChart = new() { Name = "OverviewCpuTrend", AccessibleName = "整机 CPU 最近 60 秒趋势" };
    private readonly ServerUsageTrend _serverMemoryChart = new() { Name = "OverviewMemoryTrend", AccessibleName = "物理内存最近 60 秒趋势", SeriesColor = UiTheme.Green };
    private readonly TableLayoutPanel _serverDiskRows = OverviewGrid("OverviewDiskBars");
    private readonly Dictionary<string, ServerDiskBar> _serverDiskBars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _serverDisks = UiTheme.Label("磁盘容量：采样中", 10, UiTheme.Muted);
    private readonly Label _serverErrors = UiTheme.Label("CPU 首次采样需等待下一次刷新；磁盘为容量使用率。", 10, UiTheme.Muted);

    private Control BuildOverviewPage()
    {
        var content = new TableLayoutPanel { Name = "OverviewContent", ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var cards = OverviewGrid("OverviewCards");
        var specifications = new[]
        {
            ("设备总数", _deviceCount, "全部已配置设备", UiTheme.Blue),
            ("在线设备", _onlineCount, "已连接并进入运行状态", UiTheme.Green),
            ("异常设备", _abnormalCount, "含重连、故障及在线采样异常", UiTheme.Red),
            ("停止设备", _stoppedCount, "含未启用的设备", UiTheme.Muted)
        };
        var primary = new List<TableLayoutPanel>();
        for (var i = 0; i < specifications.Length; i++)
        {
            var (title, value, detail, color) = specifications[i];
            value.Name = "OverviewDeviceMetric_" + i;
            var card = OverviewSection("OverviewCard_" + i, color,
                UiTheme.Label(title, 10, UiTheme.Muted), value, UiTheme.Label(detail, 10, UiTheme.Muted));
            primary.Add(card);
        }
        var health = OverviewGrid("OverviewHealthPanels");
        _healthState.Name = "OverviewHealthState"; _pointHealth.Name = "OverviewPointHealth";
        var healthPanels = new[]
        {
            OverviewSection("OverviewCollectionHealth", UiTheme.Green, UiTheme.Label("采集健康", 12, UiTheme.Ink, true),
                _healthState, _onlineRate, _pointHealth, _connectionHealth),
            OverviewSection("OverviewDeliveryHealth", UiTheme.Blue, UiTheme.Label("上报与输出", 12, UiTheme.Ink, true),
                _mqttStatus, _pendingCount, _outputHealth)
        };
        ArrangeOverviewGrid(cards, primary, 4);
        ArrangeOverviewGrid(health, healthPanels, 2);
        _serverCpu.Name = "OverviewServerCpu"; _serverMemory.Name = "OverviewServerMemory";
        _serverDisks.Name = "OverviewServerDisks"; _serverErrors.Name = "OverviewServerErrors";
        var resourceGrid = OverviewGrid("OverviewResourceCharts");
        var resourceRows = new[]
        {
            OverviewResourceRow("OverviewCpuCard", UiTheme.Blue, _serverCpuTitle,
                _serverCpu, _serverCpuChart),
            OverviewResourceRow("OverviewMemoryCard", UiTheme.Green, _serverMemoryTitle,
                _serverMemory, _serverMemoryChart)
        };
        var resourcePanels = resourceRows.Select(row => row.Panel).ToArray();
        ArrangeOverviewGrid(resourceGrid, resourcePanels, 2, 8);
        _serverDiskRows.ColumnCount = 1; _serverDiskRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var resources = OverviewSection("OverviewServerResources", UiTheme.Border,
            _serverIdentity, resourceGrid, _serverDisks, _serverDiskRows, _serverErrors);
        resources.Padding = new Padding(24, 8, 12, 8);
        foreach (var label in resources.Controls.OfType<Label>()) label.Margin = new Padding(0, 2, 0, 2);
        _feedbackTip.SetToolTip(_serverIdentity, "最近 60 秒趋势，每秒刷新；鼠标移到曲线上查看读数。磁盘显示容量使用率。");
        var links = UiTheme.Toolbar(
            UiTheme.Button("设备与点位", (_, _) => ShowPage("devices")),
            UiTheme.Button("实时监控", (_, _) => ShowPage("realtime")),
            UiTheme.Button("通信诊断", (_, _) => ShowPage("diagnostics")));
        links.Name = "OverviewActions";
        _overviewDeviceSummary.Dock = DockStyle.Fill;
        content.Controls.Add(cards, 0, 0); content.Controls.Add(_overviewDeviceSummary, 0, 1);
        content.Controls.Add(health, 0, 2); content.Controls.Add(links, 0, 3); content.Controls.Add(resources, 0, 4);
        var viewport = ScrollableContent(content, "OverviewViewport", () => cards.PreferredSize.Height
            + _overviewDeviceSummary.PreferredHeight + _overviewDeviceSummary.Margin.Vertical + health.PreferredSize.Height
            + links.PreferredSize.Height + resources.PreferredSize.Height + resources.Margin.Vertical);
        var arranging = false;
        void Arrange()
        {
            if (arranging || !cards.IsHandleCreated || !IsDpiLayoutReady) return;
            arranging = true;
            try
            {
                var width = content.ClientSize.Width;
                ArrangeOverviewGrid(cards, primary, width >= UiTheme.Scale(cards, 840) ? 4 : width >= UiTheme.Scale(cards, 400) ? 2 : 1);
                ArrangeOverviewGrid(health, healthPanels, width >= UiTheme.Scale(health, 620) ? 2 : 1);
                ArrangeOverviewGrid(resourceGrid, resourcePanels, width >= UiTheme.Scale(resourceGrid, 680) ? 2 : 1, 8);
                foreach (var panel in primary.Concat(healthPanels).Concat(resourceRows.Select(row => row.Summary)).Append(resources))
                    foreach (var label in panel.Controls.OfType<Label>())
                        SetWrappingWidth(label, Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal));
                SetWrappingWidth(_overviewDeviceSummary, Math.Max(1, width));
                viewport.PerformLayout();
            }
            finally { arranging = false; }
        }
        content.SizeChanged += (_, _) => Arrange();
        cards.SizeChanged += (_, _) => Arrange();
        health.SizeChanged += (_, _) => Arrange();
        resources.SizeChanged += (_, _) => Arrange();
        resourceGrid.SizeChanged += (_, _) => Arrange();
        cards.HandleCreated += (_, _) => Arrange();
        DpiLayoutCompleted += (_, _) => Arrange();
        return UiTheme.PageLayout("运行总览", "", UiTheme.Toolbar(), viewport);
    }

    private static TableLayoutPanel OverviewGrid(string name) => new()
    { Name = name, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };

    private static TableLayoutPanel OverviewSection(string name, Color accent, params Control[] controls)
    {
        var panel = UiTheme.Card(accent);
        panel.Name = name; panel.Dock = DockStyle.Fill; panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Padding = new Padding(20, 12, 16, 12); panel.Margin = new Padding(0, 0, 0, 12);
        panel.ColumnCount = 1; panel.RowCount = controls.Length;
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < controls.Length; i++)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            controls[i].Dock = DockStyle.Fill;
            panel.Controls.Add(controls[i], 0, i);
        }
        return panel;
    }

    private static (TableLayoutPanel Panel, TableLayoutPanel Summary, ServerUsageTrend Chart) OverviewResourceRow(
        string name, Color accent, Label title, Label value, ServerUsageTrend chart)
    {
        var summary = OverviewGrid(name + "Summary");
        summary.ColumnCount = 1; summary.RowCount = 2;
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var labels = new[] { title, value };
        for (var i = 0; i < labels.Length; i++)
        {
            summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            labels[i].Dock = DockStyle.Fill; labels[i].Margin = new Padding(0, 1, 0, 1);
            summary.Controls.Add(labels[i], 0, i);
        }
        var panel = UiTheme.Card(accent);
        panel.Name = name; panel.Dock = DockStyle.Fill; panel.AutoSize = true; panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Padding = new Padding(28, 6, 10, 6); panel.Margin = new Padding(0, 0, 0, 6);
        panel.ColumnCount = 1; panel.RowCount = 2;
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        chart.Dock = DockStyle.Top; chart.Margin = new Padding(0, 4, 0, 0);
        panel.Controls.Add(summary, 0, 0); panel.Controls.Add(chart, 0, 1);
        return (panel, summary, chart);
    }

    private static void ArrangeOverviewGrid(TableLayoutPanel grid, IReadOnlyList<TableLayoutPanel> panels, int columns, int gap = 12)
    {
        if (grid.ColumnCount != columns || grid.Controls.Count != panels.Count || grid.ColumnStyles.Count != columns)
        {
            grid.SuspendLayout(); grid.Controls.Clear(); grid.ColumnStyles.Clear(); grid.RowStyles.Clear();
            grid.ColumnCount = columns; grid.RowCount = (panels.Count + columns - 1) / columns;
            for (var i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (var i = 0; i < grid.RowCount; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (var i = 0; i < panels.Count; i++) grid.Controls.Add(panels[i], i % columns, i / columns);
            grid.ResumeLayout(true);
        }
        for (var i = 0; i < panels.Count; i++)
            panels[i].Margin = UiTheme.Scale(grid, new Padding(0, 0, i % columns == columns - 1 ? 0 : gap, gap));
    }

    private void RefreshOverview(GatewayOverviewSummary summary)
    {
        _deviceCount.Text = summary.TotalDevices.ToString("N0"); _onlineCount.Text = summary.OnlineDevices.ToString("N0");
        _abnormalCount.Text = summary.AbnormalDevices.ToString("N0"); _stoppedCount.Text = summary.StoppedDevices.ToString("N0");
        _overviewDeviceSummary.Text = $"启用 {summary.EnabledDevices:N0} 台 · 未启用 {summary.TotalDevices - summary.EnabledDevices:N0} 台 · 启用点位 {summary.EnabledPoints:N0} 个";
        var attention = summary.AbnormalDevices > 0 || summary.BadPoints > 0 || summary.UncertainPoints > 0;
        _healthState.Text = summary.TotalDevices == 0 ? "尚未配置设备" : attention ? "采集需要关注"
            : summary.ConnectingDevices > 0 ? "正在连接设备" : summary.OnlineDevices == 0 ? "等待启动采集"
            : summary.WaitingPoints > 0 ? "部分点位等待采集" : summary.EnabledPoints == 0 ? "尚无启用点位" : "采集运行正常";
        _healthState.ForeColor = attention ? UiTheme.Red : summary.OnlineDevices > 0 ? UiTheme.Green : UiTheme.Muted;
        _onlineRate.Text = summary.OnlinePercent is { } rate ? $"在线率 {rate:F1}%（在线 / 启用设备）" : "在线率 —（尚无启用设备）";
        _pointHealth.Text = $"正常 {summary.GoodPoints:N0} · 异常 {summary.BadPoints:N0} · 不确定 {summary.UncertainPoints:N0}\n待采集 {summary.WaitingPoints:N0}（含停止设备点位）";
        _connectionHealth.Text = $"连接中 {summary.ConnectingDevices} · 重连中 {summary.ReconnectingDevices} · 故障 {summary.FaultedDevices}";
        _mqttStatus.Text = !_configuration.Mqtt.Enabled ? "平台未启用" : _application.IsMqttConnected ? "平台已连接" : "平台未连接";
        _mqttStatus.ForeColor = _application.IsMqttConnected ? UiTheme.Green : UiTheme.Muted;
        var databases = _application.GetDatabaseStatuses(); var logging = _application.GetLoggingStatus();
        var failures = databases.Count(database => database.Enabled && database.Error is not null);
        _outputHealth.Text = $"数据库：启用 {databases.Count(database => database.Enabled)} · 异常 {failures}\n日志：{(logging.FileError is null ? "无写入错误" : "写入异常，请查看运行日志")}";
        _outputHealth.ForeColor = failures > 0 || logging.FileError is not null ? UiTheme.Red : UiTheme.Muted;
    }

    private void RefreshServerResources(ServerResourceSnapshot snapshot)
    {
        static string Load(double value) => $"{value:F1}% · {(value >= 85 ? "偏高" : "正常")}";
        _serverIdentity.Text = $"服务器资源 · {snapshot.MachineName}";
        _serverCpuTitle.Text = $"CPU · {snapshot.LogicalProcessors} 逻辑处理器";
        _serverMemoryTitle.Text = snapshot.Memory is { } totalMemory ? $"内存 · {ServerDiskBar.Capacity(totalMemory.TotalBytes)}" : "内存";
        _serverCpu.Text = snapshot.CpuUsagePercent is { } cpu ? Load(cpu) : "采样中 / 不可用";
        _serverCpu.ForeColor = snapshot.CpuUsagePercent >= 85 ? UiTheme.Warning : snapshot.CpuUsagePercent is null ? UiTheme.Muted : UiTheme.Blue;
        _serverMemory.Text = snapshot.Memory is { } memory ? Load(memory.UsedPercent) : "不可用";
        _serverMemory.ForeColor = snapshot.Memory?.UsedPercent >= 85 ? UiTheme.Warning : snapshot.Memory is null ? UiTheme.Muted : UiTheme.Green;
        if (snapshot.History.Count > 0)
        {
            // The worker's history is authoritative, including samples taken while another page was open.
            _serverCpuChart.ReplaceReadings(snapshot.History.Select(reading => new ServerUsageTrend.Reading(reading.Timestamp, reading.CpuUsagePercent)));
            _serverMemoryChart.ReplaceReadings(snapshot.History.Select(reading => new ServerUsageTrend.Reading(reading.Timestamp, reading.MemoryUsagePercent)));
        }
        else
        {
            _serverCpuChart.AddReading(snapshot.Timestamp, snapshot.CpuUsagePercent);
            _serverMemoryChart.AddReading(snapshot.Timestamp, snapshot.Memory?.UsedPercent);
        }
        var tightDisks = snapshot.Disks.Count(disk => disk.UsedPercent >= 90 && disk.Error is null);
        _serverDisks.Text = snapshot.Disks.Count == 0 ? "磁盘容量 · 暂无可用的本地固定磁盘读数"
            : $"磁盘容量 · {snapshot.Disks.Count} 个固定磁盘{(tightDisks > 0 ? $" · {tightDisks} 个空间偏紧" : "")}";
        _serverDisks.ForeColor = snapshot.Disks.Any(disk => disk.UsedPercent >= 90 || disk.Error is not null) ? UiTheme.Warning : UiTheme.Muted;
        RefreshServerDiskBars(snapshot.Disks);
        var errors = snapshot.Errors.Concat(snapshot.Disks.Where(disk => disk.Error is not null).Select(disk => $"{disk.Name}：{disk.Error}")).ToArray();
        _serverErrors.Text = $"更新于 {snapshot.Timestamp.ToLocalTime():HH:mm:ss} · 最近 60 秒趋势 · 磁盘 90% 起提示空间偏紧。"
            + (errors.Length > 0 ? "\n" + string.Join("；", errors) : "");
        _serverErrors.ForeColor = errors.Length > 0 ? UiTheme.Warning : UiTheme.Muted;
    }

    private void RefreshServerDiskBars(IReadOnlyList<ServerDiskUsage> disks)
    {
        var names = disks.Select(disk => disk.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = !_serverDiskBars.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(names);
        _serverDiskRows.SuspendLayout();
        try
        {
            if (changed)
            {
                foreach (var name in _serverDiskBars.Keys.Where(name => !names.Contains(name)).ToArray())
                {
                    var removed = _serverDiskBars[name];
                    _serverDiskRows.Controls.Remove(removed); removed.Dispose(); _serverDiskBars.Remove(name);
                }
                _serverDiskRows.RowStyles.Clear(); _serverDiskRows.RowCount = disks.Count;
            }
            var row = 0;
            foreach (var disk in disks.OrderBy(disk => disk.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!_serverDiskBars.TryGetValue(disk.Name, out var bar))
                {
                    bar = new ServerDiskBar(disk) { Name = "OverviewDisk_" + disk.Name, Dock = DockStyle.Top };
                    _serverDiskBars.Add(disk.Name, bar); _serverDiskRows.Controls.Add(bar, 0, row);
                }
                if (changed)
                {
                    _serverDiskRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    _serverDiskRows.SetCellPosition(bar, new TableLayoutPanelCellPosition(0, row));
                }
                bar.UpdateUsage(disk); row++;
            }
        }
        finally { _serverDiskRows.ResumeLayout(true); }
    }

    private void ShowServerResourceFailure(string error)
    {
        _serverCpu.Text = "整机 CPU：不可用"; _serverMemory.Text = "物理内存：不可用"; _serverDisks.Text = "磁盘容量：不可用";
        _serverCpu.ForeColor = _serverMemory.ForeColor = _serverDisks.ForeColor = UiTheme.Muted;
        var now = DateTimeOffset.UtcNow;
        _serverCpuChart.AddReading(now, null); _serverMemoryChart.AddReading(now, null);
        foreach (var (name, bar) in _serverDiskBars) bar.UpdateUsage(new(name, null, null, error));
        _serverErrors.Text = "资源读取失败：" + error; _serverErrors.ForeColor = UiTheme.Warning;
    }

    private async Task RefreshServerResourcesAsync()
    {
        try
        {
            var snapshot = await _application.GetServerResourcesAsync();
            if (!_closing && !IsDisposed) RefreshServerResources(snapshot);
        }
        catch (Exception exception)
        {
            if (!_closing && !IsDisposed) ShowServerResourceFailure(exception.Message);
        }
    }
}
