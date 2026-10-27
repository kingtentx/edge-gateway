using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeOverviewAsync()
    {
        if (_serverResourceRefresh is not null) await _serverResourceRefresh;
        var actual = await _application.GetServerResourcesAsync();
        if (actual.Memory is null || actual.Memory.TotalBytes <= 0 || actual.Disks.Count == 0)
            throw new InvalidOperationException("Overview host memory/disk resource wiring is unavailable.");
        var summary = _application.GetOverviewSummary();
        try
        {
            RefreshOverview(new(12, 10, 7, 2, 3, 1, 1, 0, 100, 70, 5, 2, 23));
            if (_deviceCount.Text != "12" || _onlineCount.Text != "7" || _abnormalCount.Text != "2"
                || _stoppedCount.Text != "3" || _healthState.Text != "采集需要关注")
                throw new InvalidOperationException("Overview did not render device and quality summaries.");
            if (Descendants(_pages["overview"]).OfType<Control>().Any(control => control.Name is "OverviewPointHealth" or "OverviewConnectionHealth")
                || _outputHealth.Text.Contains("日志：", StringComparison.Ordinal))
                throw new InvalidOperationException("Overview still shows removed collection or log status details.");
            RefreshServerResources(new(DateTimeOffset.UtcNow, "resource-fixture", 8, 95,
                new(16L << 30, 1L << 30), [new("C:\\", 100L << 30, 5L << 30)], []));
            if (_serverCpu.Text != "95.0%" || !_serverMemory.Text.EndsWith('%') || _serverMemory.Text.Contains("正常", StringComparison.Ordinal))
                throw new InvalidOperationException("CPU and memory values must contain percentages without status suffixes.");
            if (_serverCpu.ForeColor != UiTheme.Warning || _serverMemory.ForeColor != UiTheme.Warning
                || _serverDisks.ForeColor != UiTheme.Warning || !_serverDisks.Text.Contains("空间偏紧"))
                throw new InvalidOperationException("Overview did not identify high resource usage.");
            if (_serverIdentity.Text.Contains("逻辑处理器") || _serverCpuTitle.Text != "CPU · 8 逻辑处理器"
                || _serverMemoryTitle.Text != "内存 · " + ServerDiskBar.Capacity(16L << 30))
                throw new InvalidOperationException("CPU count and total memory must be displayed in their own card titles.");
            var removedHints = new[] { "设备连接、采集质量与数据输出，一屏掌握网关健康情况。", "消息确认后清理补传队列；输出错误请查看通信诊断。", "整机平均 · 85% 起偏高", "已用 " };
            if (Descendants(_pages["overview"]).OfType<Label>().Any(label => removedHints.Any(hint => label.Text.StartsWith(hint))))
                throw new InvalidOperationException("Overview still contains a removed explanatory label.");
            ShowServerResourceFailure("fixture unavailable");
            if (_serverCpu.Text.Contains("95") || !_serverMemory.Text.Contains("不可用") || !_serverDisks.Text.Contains("不可用"))
                throw new InvalidOperationException("Overview reused stale resource values after an error.");
        }
        finally { RefreshOverview(summary); RefreshServerResources(actual); }
        VerifyOverviewCards("baseline overview");
        ResetScrollPositions(_pages["overview"], navigationMenu);
        Console.WriteLine("Overview health smoke passed: four device metrics, no device list, live host memory/disks, resource warnings/unavailable states and layout reachability.");
    }

    private async Task SmokeServerResourceChartsAsync()
    {
        if (_application.IsRunning) throw new InvalidOperationException("Resource chart verification must not start device collection.");
        await SmokeOverviewAsync();
        var directory = Path.GetDirectoryName(Path.GetFullPath(_smokeScreenshotPath ?? "ui-smoke.png"))!;
        using var focusDiagnostics = StartSmokeFocusDiagnostics(directory);
        var beforePageSwitch = await _application.GetServerResourcesAsync();
        ShowPage("devices");
        await Task.Delay(3200);
        var afterPageSwitch = await _application.GetServerResourcesAsync();
        ShowPage("overview");
        if (afterPageSwitch.History.Count(reading => reading.Timestamp > beforePageSwitch.Timestamp) < 2)
            throw new InvalidOperationException("Worker did not keep sampling while the overview was hidden.");
        RefreshServerResources(afterPageSwitch);
        var now = DateTimeOffset.UtcNow;
        _serverCpuChart.ClearHistory(); _serverMemoryChart.ClearHistory();
        for (var second = -90; second <= 0; second++)
        {
            var timestamp = now.AddSeconds(second);
            _serverCpuChart.AddReading(timestamp, 45 + 25 * Math.Sin(second / 6d));
            _serverMemoryChart.AddReading(timestamp, 55 + second / 20d);
        }
        if (_serverCpuChart.Readings.Count != 61 || _serverCpuChart.Readings[0].Timestamp != now.AddSeconds(-60)
            || _serverCpuChart.Readings.Any(reading => reading.Percent is null))
            throw new InvalidOperationException("Resource trend did not retain the exact continuous time window.");
        var recoveredHistory = Enumerable.Range(-60, 61).Select(second => new ServerResourceReading(
            now.AddSeconds(second), 45 + 25 * Math.Sin(second / 6d), 55 + second / 20d)).ToArray();
        ShowServerResourceFailure("示例：界面连接暂时中断");
        RefreshServerResources(new(now, "图表验证示例", 8, 45, new(16L << 30, 7L << 30),
            [new("C:\\", 100L << 30, 5L << 30), new("D:\\", 500L << 30, 300L << 30), new("E:\\", null, null, "示例：磁盘暂不可用"),
                new("F:\\", 3000L << 30, 1728L << 30)], []) { History = recoveredHistory });
        if (_serverCpuChart.Readings.Count != 61 || _serverCpuChart.Readings.Any(reading => reading.Percent is null))
            throw new InvalidOperationException("Real worker history did not repair a temporary UI refresh gap.");
        if (_serverDiskBars.Count != 4 || _serverDiskBars["E:\\"].UsedPercent is not null || !_serverErrors.Text.Contains("磁盘暂不可用"))
            throw new InvalidOperationException("Partial disk failure was hidden or replaced by a numeric reading.");
        var retained = _serverDiskBars["D:\\"];
        RefreshServerDiskBars([new("D:\\", 500L << 30, 250L << 30), new("C:\\", 100L << 30, 5L << 30),
            new("E:\\", null, null, "示例：磁盘暂不可用"), new("F:\\", 3000L << 30, 1728L << 30)]);
        if (!ReferenceEquals(retained, _serverDiskBars["D:\\"]) || _serverDiskBars["D:\\"].UsedPercent != 50)
            throw new InvalidOperationException("Disk refresh lost stable drive identity.");
        var originalBounds = Bounds; var originalMinimum = MinimumSize; var originalMaximum = MaximumSize;
        try
        {
            foreach (var (name, logical) in new[] { ("wide", new Size(1200, 800)), ("narrow", new Size(740, 560)) })
            {
                var area = new Rectangle(Screen.FromControl(this).WorkingArea.Location,
                    new Size(UiTheme.Scale(this, logical.Width), UiTheme.Scale(this, logical.Height)));
                MinimumSize = Size.Empty; MaximumSize = area.Size;
                if (!SetWindowPos(Handle, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, 0x0014))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                FitToWorkingArea(testWorkArea: area);
                await Task.Yield(); PerformLayoutRecursively(this);
                VerifyOverviewCards("server resources " + name);
                var cpuPanel = _serverCpuChart.Parent!;
                var memoryPanel = _serverMemoryChart.Parent!;
                var resourceGrid = (TableLayoutPanel)cpuPanel.Parent!;
                if (name == "wide" && (resourceGrid.GetColumn(cpuPanel) != 0 || resourceGrid.GetColumn(memoryPanel) != 1
                    || resourceGrid.GetRow(cpuPanel) != resourceGrid.GetRow(memoryPanel)))
                    throw new InvalidOperationException("Wide resource cards must display CPU on the left and memory on the right.");
                if (name == "narrow" && resourceGrid.ColumnCount != 1)
                    throw new InvalidOperationException("Narrow resource cards must remain readable in one column.");
                foreach (var chart in new Control[] { _serverCpuChart, _serverMemoryChart })
                {
                    if (chart.Height < UiTheme.Scale(chart, 120)) throw new InvalidOperationException("Resource chart did not retain its increased height.");
                    BringIntoViewAndAssert(chart, "server resources " + name + " trend");
                }
                SaveSmokeScreenshot(this, Path.Combine(directory, $"server-resources-{name}-trends.png"));
                if (name == "wide" && _serverDiskRows.ColumnCount < 2)
                    throw new InvalidOperationException("Wide disk capacity cards must use multiple columns.");
                if (name == "narrow" && _serverDiskRows.ColumnCount != 1)
                    throw new InvalidOperationException("Narrow disk capacity cards must wrap to one column.");
                var diskCards = _serverDiskBars.Values.ToArray();
                foreach (var bar in diskCards)
                {
                    BringIntoViewAndAssert(bar, "server resources " + name + " disk");
                    if (name == "wide" && bar.Width > _serverDiskRows.ClientSize.Width * 0.6)
                        throw new InvalidOperationException("A disk capacity bar still stretches across the resource area.");
                    foreach (var other in diskCards.Where(other => !ReferenceEquals(other, bar)))
                        if (bar.Bounds.IntersectsWith(other.Bounds)) throw new InvalidOperationException("Disk capacity cards overlap.");
                }
                BringIntoViewAndAssert(_serverErrors, "server resources " + name + " errors");
                SaveSmokeScreenshot(this, Path.Combine(directory, $"server-resources-{name}-disks.png"));
                var resourcePanel = Descendants(_pages["overview"]).Single(control => control.Name == "OverviewServerResources");
                Console.WriteLine($"Resource viewport passed: {name}; logical work area {logical}; actual DPI {DeviceDpi}; actual bounds {Bounds}; resource area height {resourcePanel.Height * 96d / DeviceDpi:F1} logical pixels.");
            }
            ShowServerResourceFailure("示例：采样失败");
            if (_serverCpuChart.Readings[^1].Percent is not null || _serverMemoryChart.Readings[^1].Percent is not null
                || _serverDiskBars.Values.Any(bar => bar.UsedPercent is not null))
                throw new InvalidOperationException("Failed resource refresh retained current numeric values.");
            RefreshServerDiskBars([new("D:\\", 500L << 30, 250L << 30)]);
            if (_serverDiskBars.Count != 1 || !ReferenceEquals(retained, _serverDiskBars["D:\\"]))
                throw new InvalidOperationException("Removed disks were retained or surviving drive identity changed.");
        }
        finally
        {
            MinimumSize = originalMinimum; MaximumSize = originalMaximum; Bounds = originalBounds;
            FitToWorkingArea(); _serverCpuChart.ClearHistory(); _serverMemoryChart.ClearHistory();
            RefreshServerResources(await _application.GetServerResourcesAsync());
        }
        Console.WriteLine("Server resource charts smoke passed: real host wiring; background sampling across page changes; continuous 60-second history and refresh-gap recovery; disk identity/removal; partial/total failure; wide/narrow reachability and four screenshots. Layout sizes are simulated at the host's actual DPI.");
    }
}
