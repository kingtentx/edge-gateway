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
            Require(_trend.Datasets.Count == 2 && _trend.Datasets.Sum(series => series.DataPoints.Count) == 22,
                "Official chart joined across a bad sample or lost valid points.");
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
            // Availability is evaluated from the applied configuration, never by attempting a write.
            var connection = new DatabaseConnectionConfiguration
                { Name = "历史数据库", Provider = ExternalDatabaseProvider.PostgreSql, Enabled = true, Username = "test" };
            var group = new DatabaseWriteGroupConfiguration
            {
                Name = "逐点映射历史", ConnectionId = connection.Id, Enabled = true, Mode = DatabaseStorageMode.PointMapping,
                Columns = new[] { DatabaseValueSource.DeviceId, DatabaseValueSource.TagId, DatabaseValueSource.SampleTime,
                    DatabaseValueSource.EngineeringValue, DatabaseValueSource.Quality }
                    .Select(source => new DatabaseColumnMapping { Column = source.ToString(), Source = source }).ToList()
            };
            CompleteConfigurationCommit(await _application.ApplyDatabaseSettingsAsync([connection], [group]));
            _historySource.SelectedIndex = 1;
            SelectChoice(_historyGroup, group.Id);
            Require(_historyQuery.Enabled && (_historyGroup.SelectedItem as Choice)?.Id == group.Id,
                "Compatible PointMapping group was excluded from external history.");
            SaveEditorScreenshot(this, "history-sources", "external-point-mapping");
            var chosen = group.Id;
            BindHistoryGroups();
            Require((_historyGroup.SelectedItem as Choice)?.Id == chosen && _historyQuery.Enabled,
                "History group selection was lost on rebind.");
            group.Columns.RemoveAll(column => column.Source == DatabaseValueSource.Quality);
            CompleteConfigurationCommit(await _application.ApplyDatabaseSettingsAsync([connection], [group]));
            Require((_historyGroup.SelectedItem as Choice)?.Id == chosen && !_historyQuery.Enabled
                && _historySummary.Text.Contains("质量"), "Missing quality mapping was hidden or not explained.");
            SaveEditorScreenshot(this, "history-sources", "external-missing-quality");
            group.Mode = DatabaseStorageMode.PointRows; group.Columns.Clear();
            CompleteConfigurationCommit(await _application.ApplyDatabaseSettingsAsync([connection], [group]));
            Require(_historyQuery.Enabled, "Standard point-row history regressed.");
            connection.Enabled = false;
            CompleteConfigurationCommit(await _application.ApplyDatabaseSettingsAsync([connection], [group]));
            Require(!_historyQuery.Enabled && _historySummary.Text.Contains("连接未启用"), "Disabled connection reason was lost.");
            connection.Enabled = true; group.Enabled = false;
            CompleteConfigurationCommit(await _application.ApplyDatabaseSettingsAsync([connection], [group]));
            Require(!_historyQuery.Enabled && _historySummary.Text.Contains("写入组未启用"), "Disabled group reason was lost.");
            _historySource.SelectedIndex = 0;
            Require(_historyQuery.Enabled, "External source restrictions leaked into local-log history.");
            Console.WriteLine($"HistorySources smoke passed: DeviceDpi={DeviceDpi}; local logs without database, quality, trend, pagination, captured CSV, source/scan changes, empty results; mapped/standard groups, missing-quality and disabled reasons, selection retention; no external database contacted.");
        }
        finally { await CommitConfigurationAsync(original, replaceCloudDraft: true); }
    }
}
