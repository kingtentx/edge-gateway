using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeHistorySourcesAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("HistorySources requires an isolated stopped host.");
        _refreshTimer.Stop();
        var original = Clone(_configuration);
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt.Enabled = false;
            fixture.Channels = []; fixture.Databases = []; fixture.DatabaseWriteGroups = [];
            var device = new DeviceConfiguration { Name = "日志趋势验证", DriverId = "simulator",
                Tags = [new() { Name = "温度", Address = "sine", DataType = TagDataType.Float64 }] };
            fixture.Devices = [device];
            await CommitConfigurationAsync(fixture, replaceCloudDraft: true);
            ShowPage("history");
            SelectChoice(_historyDevice, device.Id); SelectChoice(_historyTag, device.Tags[0].Id);
            Require(HistoryFromLogs && _historyQuery.Enabled, "No-database log query is unavailable.");
            var now = DateTimeOffset.Now.AddMinutes(-1);
            var log = (IConfigurableGatewayLog)_log;
            for (var i = 0; i < 23; i++)
                log.WriteSample(new(device.Id, device.Name, device.Tags[0].Id, "温度", "sine", i,
                    i == 11 ? null : i, i == 11 ? SampleQuality.Bad : SampleQuality.Good,
                    now.AddSeconds(i), "℃", i == 11 ? "读取超时" : null));
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(100); await QueryHistoryAsync();
                if (_queriedHistory.Count == 23) break;
            }
            Require(_queriedHistory.Count == 23 && _trend.Visible && !_historyConditionsChanged,
                "Log query did not populate the trend snapshot.");
            Require(_queriedHistory.Count(s => s.Quality == SampleQuality.Bad && s.Value is null) == 1,
                "Bad sample was replaced by a good value.");
            PerformLayoutRecursively(this);
            Require(_historyPageView.ContentLayout.RowStyles[0].SizeType == SizeType.AutoSize
                && _trend.Height > UiTheme.Scale(_trend, 100), "Trend damaged the title or chart layout.");
            GridPagination.GetPager(_historyGrid).Current = 3;
            var csv = BuildHistoryCsv();
            Require(csv.Contains("采样日志") && csv.Contains("不代表完整历史")
                && csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length == 25,
                "CSV omitted metadata or off-page samples.");
            SaveEditorScreenshot(this, "history-sources", "log-trend");
            _historySource.SelectedIndex = 1;
            Require(!_historyQuery.Enabled && _historyConditionsChanged && BuildHistoryCsv() == csv,
                "Changing source relabeled the executed snapshot or enabled an unconfigured database query.");
            SaveEditorScreenshot(this, "history-sources", "external-unconfigured");
            _historySource.SelectedIndex = 0; _historyScanLimit.Value = 16;
            Require(_historyConditionsChanged && BuildHistoryCsv() == csv, "Changing scan limit changed export.");
            _historyFrom.Value = DateTime.Now.AddDays(-2); _historyTo.Value = DateTime.Now.AddDays(-1);
            await QueryHistoryAsync();
            Require(_queriedHistory.Count == 0 && !_historyExport.Enabled && _historySummary.Text.Contains("未找到"),
                "Empty query kept stale results or an enabled export.");
            SaveEditorScreenshot(this, "history-sources", "empty");
            Console.WriteLine($"HistorySources smoke passed: DeviceDpi={DeviceDpi}; local logs without database, quality, trend, pagination, captured CSV, source/scan changes, empty results.");
        }
        finally { await CommitConfigurationAsync(original, replaceCloudDraft: true); }
    }
}
