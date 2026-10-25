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
            RefreshServerResources(new(DateTimeOffset.UtcNow, "resource-fixture", 8, 95,
                new(16L << 30, 1L << 30), [new("C:\\", 100L << 30, 5L << 30)], []));
            if (_serverCpu.ForeColor != UiTheme.Warning || _serverMemory.ForeColor != UiTheme.Warning
                || _serverDisks.ForeColor != UiTheme.Warning || !_serverDisks.Text.Contains("空间偏紧"))
                throw new InvalidOperationException("Overview did not identify high resource usage.");
            ShowServerResourceFailure("fixture unavailable");
            if (_serverCpu.Text.Contains("95") || !_serverMemory.Text.Contains("不可用") || !_serverDisks.Text.Contains("不可用"))
                throw new InvalidOperationException("Overview reused stale resource values after an error.");
        }
        finally { RefreshOverview(summary); RefreshServerResources(actual); }
        VerifyOverviewCards("baseline overview");
        ResetScrollPositions(_pages["overview"], _navigation["overview"]);
        Console.WriteLine("Overview health smoke passed: four device metrics, no device list, live host memory/disks, resource warnings/unavailable states and layout reachability.");
    }
}
