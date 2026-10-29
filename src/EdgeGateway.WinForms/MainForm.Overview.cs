using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private Task? _serverResourceRefresh;
    private OverviewPageView _overviewPageView = null!;
    private Label _deviceCount = null!;
    private Label _onlineCount = null!;
    private Label _abnormalCount = null!;
    private Label _stoppedCount = null!;
    private Label _overviewDeviceSummary = null!;
    private Label _healthState = null!;
    private Label _onlineRate = null!;
    private Label _pendingCount = null!;
    private Label _mqttStatus = null!;
    private Label _outputHealth = null!;
    private Label _serverIdentity = null!;
    private Label _serverCpu = null!;
    private Label _serverMemory = null!;
    private Label _serverCpuTitle = null!;
    private Label _serverMemoryTitle = null!;
    private ServerUsageTrend _serverCpuChart = null!;
    private ServerUsageTrend _serverMemoryChart = null!;
    private TableLayoutPanel _serverDiskRows = null!;
    private readonly Dictionary<string, ServerDiskBar> _serverDiskBars = new(StringComparer.OrdinalIgnoreCase);
    private Label _serverErrors = null!;

    private Control BuildOverviewPage()
    {
        _overviewPageView = new OverviewPageView();
        _deviceCount = _overviewPageView._deviceCount;
        _onlineCount = _overviewPageView._onlineCount;
        _abnormalCount = _overviewPageView._abnormalCount;
        _stoppedCount = _overviewPageView._stoppedCount;
        _overviewDeviceSummary = _overviewPageView._overviewDeviceSummary;
        _healthState = _overviewPageView._healthState;
        _onlineRate = _overviewPageView._onlineRate;
        _pendingCount = _overviewPageView._pendingCount;
        _mqttStatus = _overviewPageView._mqttStatus;
        _outputHealth = _overviewPageView._outputHealth;
        _serverIdentity = _overviewPageView._serverIdentity;
        _serverCpu = _overviewPageView._serverCpu;
        _serverMemory = _overviewPageView._serverMemory;
        _serverCpuTitle = _overviewPageView._serverCpuTitle;
        _serverMemoryTitle = _overviewPageView._serverMemoryTitle;
        _serverErrors = _overviewPageView._serverErrors;
        _serverCpuChart = _overviewPageView._serverCpuChart;
        _serverMemoryChart = _overviewPageView._serverMemoryChart;
        _serverDiskRows = _overviewPageView._serverDiskRows;
        var content = _overviewPageView.dashboardContent;
        var cards = _overviewPageView.cards; var health = _overviewPageView.health;
        var resourceGrid = _overviewPageView.resourceGrid; var resources = _overviewPageView.resources;
        var viewport = _overviewPageView.viewport;
        Control[] primary = [_overviewPageView.metricCard0, _overviewPageView.metricCard1, _overviewPageView.metricCard2, _overviewPageView.metricCard3];
        Control[] healthPanels = [_overviewPageView.collectionCard, _overviewPageView.deliveryCard];
        Control[] resourcePanels = [_overviewPageView.cpuCard, _overviewPageView.memoryCard];
        TableLayoutPanel[] resourceSummaries = [_overviewPageView.cpuSummary, _overviewPageView.memorySummary];
        viewport.BindLayout(content, () => cards.PreferredSize.Height
            + _overviewDeviceSummary.PreferredHeight + _overviewDeviceSummary.Margin.Vertical + health.PreferredSize.Height
            + resources.PreferredSize.Height + resources.Margin.Vertical);
        _feedbackTip.SetTip(_serverIdentity, "最近 60 秒趋势，每秒刷新；鼠标移到曲线上查看读数。磁盘显示容量使用率。");
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
                ArrangeDiskGrid();
                foreach (var panel in primary.Concat(healthPanels).Concat(resourceSummaries).Append(resources))
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
        _serverDiskRows.SizeChanged += (_, _) => Arrange();
        cards.HandleCreated += (_, _) => Arrange();
        DpiLayoutCompleted += (_, _) => Arrange();
        return _overviewPageView;
    }

    private static void ArrangeOverviewGrid(TableLayoutPanel grid, IReadOnlyList<Control> panels, int columns, int gap = 12)
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
        _deviceCount.Text = summary.TotalDevices.ToString("F0"); _onlineCount.Text = summary.OnlineDevices.ToString("F0");
        _abnormalCount.Text = summary.AbnormalDevices.ToString("F0"); _stoppedCount.Text = summary.StoppedDevices.ToString("F0");
        _overviewDeviceSummary.Text = $"启用 {summary.EnabledDevices:F0} 台 · 未启用 {summary.TotalDevices - summary.EnabledDevices:F0} 台 · 启用点位 {summary.EnabledPoints:F0} 个";
        var attention = summary.AbnormalDevices > 0 || summary.BadPoints > 0 || summary.UncertainPoints > 0;
        _healthState.Text = summary.TotalDevices == 0 ? "尚未配置设备" : attention ? "采集需要关注"
            : summary.ConnectingDevices > 0 ? "正在连接设备" : summary.OnlineDevices == 0 ? "等待启动采集"
            : summary.WaitingPoints > 0 ? "部分点位等待采集" : summary.EnabledPoints == 0 ? "尚无启用点位" : "采集运行正常";
        _healthState.ForeColor = attention ? UiTheme.Red : summary.OnlineDevices > 0 ? UiTheme.Green : UiTheme.Muted;
        _onlineRate.Text = summary.OnlinePercent is { } rate ? $"在线率 {rate:F1}%（在线 / 启用设备）" : "在线率 —（尚无启用设备）";
        _mqttStatus.Text = !_configuration.Mqtt.Enabled ? "平台未启用" : _application.IsMqttConnected ? "平台已连接" : "平台未连接";
        _mqttStatus.ForeColor = _application.IsMqttConnected ? UiTheme.Green : UiTheme.Muted;
        var databases = _application.GetDatabaseStatuses();
        var failures = databases.Count(database => database.Enabled && database.Error is not null);
        _outputHealth.Text = $"数据库：启用 {databases.Count(database => database.Enabled)} · 异常 {failures}";
        _outputHealth.ForeColor = failures > 0 ? UiTheme.Red : UiTheme.Muted;
    }

    private void RefreshServerResources(ServerResourceSnapshot snapshot)
    {
        static string Load(double value) => $"{value:F1}%";
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
        RefreshServerDiskBars(snapshot.Disks);
        var errors = snapshot.Errors.Concat(snapshot.Disks.Where(disk => disk.Error is not null).Select(disk => $"{disk.Name}：{disk.Error}")).ToArray();
        _serverErrors.Text = string.Join("；", errors);
        _serverErrors.Visible = errors.Length > 0;
        _serverErrors.ForeColor = UiTheme.Warning;
    }

    private void RefreshServerDiskBars(IReadOnlyList<ServerDiskUsage> disks)
    {
        var names = disks.Select(disk => disk.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _serverDiskRows.SuspendLayout();
        try
        {
            foreach (var name in _serverDiskBars.Keys.Where(name => !names.Contains(name)).ToArray())
            {
                var removed = _serverDiskBars[name];
                _serverDiskRows.Controls.Remove(removed); removed.Dispose(); _serverDiskBars.Remove(name);
            }
            foreach (var disk in disks)
            {
                if (!_serverDiskBars.TryGetValue(disk.Name, out var bar))
                {
                    bar = new ServerDiskBar(disk) { Name = "OverviewDisk_" + disk.Name, Dock = DockStyle.Top };
                    _serverDiskBars.Add(disk.Name, bar);
                }
                bar.UpdateUsage(disk);
            }
            ArrangeDiskGrid();
        }
        finally { _serverDiskRows.ResumeLayout(true); }
    }

    private void ArrangeDiskGrid()
    {
        var width = _serverDiskRows.ClientSize.Width;
        var columns = width >= UiTheme.Scale(_serverDiskRows, 800) ? 4
            : width >= UiTheme.Scale(_serverDiskRows, 420) ? 2 : 1;
        var bars = _serverDiskBars.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Value).ToArray();
        var rebuild = _serverDiskRows.ColumnCount != columns || _serverDiskRows.ColumnStyles.Count != columns
            || _serverDiskRows.Controls.Count != bars.Length || bars.Where((bar, index) =>
                bar.Parent != _serverDiskRows || _serverDiskRows.GetColumn(bar) != index % columns
                    || _serverDiskRows.GetRow(bar) != index / columns).Any();
        if (rebuild)
        {
            _serverDiskRows.SuspendLayout();
            try
            {
                _serverDiskRows.Controls.Clear(); _serverDiskRows.ColumnStyles.Clear(); _serverDiskRows.RowStyles.Clear();
                _serverDiskRows.ColumnCount = columns; _serverDiskRows.RowCount = (bars.Length + columns - 1) / columns;
                for (var column = 0; column < columns; column++) _serverDiskRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
                for (var row = 0; row < _serverDiskRows.RowCount; row++) _serverDiskRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                for (var index = 0; index < bars.Length; index++) _serverDiskRows.Controls.Add(bars[index], index % columns, index / columns);
            }
            finally { _serverDiskRows.ResumeLayout(true); }
        }
        for (var index = 0; index < bars.Length; index++)
            bars[index].Margin = UiTheme.Scale(_serverDiskRows, new Padding(0, 0, index % columns == columns - 1 ? 0 : 10, 8));
    }

    private void ShowServerResourceFailure(string error)
    {
        _serverCpu.Text = _serverMemory.Text = "不可用";
        _serverCpu.ForeColor = _serverMemory.ForeColor = UiTheme.Muted;
        var now = DateTimeOffset.UtcNow;
        _serverCpuChart.AddReading(now, null); _serverMemoryChart.AddReading(now, null);
        foreach (var (name, bar) in _serverDiskBars) bar.UpdateUsage(new(name, null, null, error));
        _serverErrors.Visible = true;
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
