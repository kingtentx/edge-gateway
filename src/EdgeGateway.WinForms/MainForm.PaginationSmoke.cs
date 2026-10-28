using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokePaginationAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("Pagination requires an isolated stopped host.");
        _refreshTimer.Stop();
        var original = Clone(_configuration);
        static AntdUI.Pagination Pager(GatewayTable grid) => GridPagination.GetPager(grid);
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt.Enabled = false;
            fixture.Channels = []; fixture.Databases = []; fixture.DatabaseWriteGroups = [];
            fixture.Devices = Enumerable.Range(1, 23).Select(i => new DeviceConfiguration
            {
                Name = $"分页设备 {i:000}", DriverId = "simulator",
                Tags = Enumerable.Range(1, 23).Select(j => new TagConfiguration
                { Name = $"点位 {j:000}", Address = "constant:" + j, DataType = TagDataType.Float64 }).ToList()
            }).ToList();
            await CommitConfigurationAsync(fixture, replaceCloudDraft: true);
            ShowPage("devices"); PerformLayoutRecursively(this);
            await Task.Delay(100);
            var devicePager = Pager(_deviceGrid);
            Require(devicePager.Total == 23 && _deviceGrid.Rows.Count == 10, "Device first page/count incorrect.");
            devicePager.Current = 2;
            Require(SelectedDevice()?.Id == fixture.Devices[10].Id, "Page two selection has wrong stable device ID.");
            Require(_deviceGrid.Rows[0].DataBoundItem is DeviceRow { Id: var id } && id == fixture.Devices[10].Id,
                "Page two command target incorrect.");
            SaveEditorScreenshot(this, "pagination", "devices-page2");
            BindDeviceRows(fixture.Devices[22].Id);
            Require(devicePager.Current == 3 && SelectedDevice()?.Id == fixture.Devices[22].Id, "Preferred device did not reveal its page.");
            fixture.Devices.RemoveRange(20, 3);
            await CommitConfigurationAsync(fixture, replaceCloudDraft: true);
            Require(devicePager.Current == 2 && _deviceGrid.Rows.Count == 10, "Deleting final page did not clamp page.");
            devicePager.PageSize = 20;
            Require(devicePager.Current == 1 && _deviceGrid.Rows.Count == 20, "Page size did not update data.");
            _deviceSearch.Text = "no-such-device";
            Require(devicePager.Total == 0 && _deviceGrid.Rows.Count == 0 && devicePager.Current == 1, "Empty filter retained stale rows.");
            _deviceSearch.Clear();
            ShowPage("realtime");
            var device = fixture.Devices[0];
            var now = DateTimeOffset.Now;
            var samples = device.Tags.Select((tag, i) => new TelemetrySample(device.Id, device.Name, tag.Id, tag.Name,
                tag.Address, i, i, SampleQuality.Good, now, tag.Unit)).ToArray();
            RefreshRealtime(samples); PerformLayoutRecursively(this); await Task.Delay(100);
            var realtimePager = Pager(_realtimeGrid);
            realtimePager.Current = 2;
            var source = _realtimeGrid.DataSource;
            var selected = ((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample.TagId;
            RefreshRealtime(samples.Select(s => s with { Value = 99, Timestamp = now.AddSeconds(1) }).ToArray());
            Require(realtimePager.Current == 2 && ReferenceEquals(source, _realtimeGrid.DataSource)
                && ((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample.TagId == selected,
                "Realtime refresh lost page, source or selected ID.");
            SaveEditorScreenshot(this, "pagination", "realtime-page2");
            ShowPage("history");
            PresentHistoryResults(null, "分页验证", device.Name, "全部点位", new HistoryQuery(device.Id, null, now.AddMinutes(-1), now.AddMinutes(1), 100), samples);
            PerformLayoutRecursively(this); await Task.Delay(100);
            Pager(_historyGrid).Current = 3;
            Require(_historyGrid.Rows.Count == 3 && _queriedHistory.Count == 23, "History pagination changed export snapshot.");
            var csv = BuildHistoryCsv();
            Require(csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length == 24,
                "History CSV exports only the visible page.");
            SaveEditorScreenshot(this, "pagination", "history-last-page");
            // A three-page fixture cannot reveal the wide-window page-number expansion.
            var longHistory = Enumerable.Range(0, 500).Select(i => samples[i % samples.Length] with
                { Timestamp = now.AddMilliseconds(i) }).ToArray();
            PresentHistoryResults(null, "分页验证", device.Name, "全部点位",
                new HistoryQuery(device.Id, null, now.AddMinutes(-1), now.AddMinutes(1), 500), longHistory);
            var historyPager = Pager(_historyGrid);
            historyPager.Current = 12;
            PerformLayoutRecursively(this);
            var pageButtons = (Array)typeof(AntdUI.Pagination).GetField("buttons",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(historyPager)!;
            Require(historyPager.PageTotal == 50 && pageButtons.Length <= 10
                && ((SampleRow)_historyGrid.Rows[0].DataBoundItem!).Sample.Timestamp == longHistory[110].Timestamp,
                "Long-list pagination expanded beyond eight page slots or selected the wrong data.");
            SaveEditorScreenshot(this, "pagination", "history-50-pages-compact");
            PresentHistoryResults(null, "分页验证", device.Name, "全部点位",
                new HistoryQuery(device.Id, null, now.AddMinutes(-1), now.AddMinutes(1), 500), longHistory.Take(147).ToArray());
            PerformLayoutRecursively(this);
            var footer = (TableLayoutPanel)historyPager.Parent!;
            var totalLabel = footer.Controls.OfType<Label>().Single();
            var sizeSelect = footer.Controls.OfType<ComboBox>().Single();
            Require(totalLabel.Text == "共 147 条" && totalLabel.Right < sizeSelect.Left
                && sizeSelect.Right < historyPager.Left && !historyPager.ShowSizeChanger,
                "Footer must order total, page size and page numbers from left to right.");
            Require(footer.GetColumnWidths()[0] > 0
                && Math.Abs(totalLabel.Left - footer.GetColumnWidths()[0]) <= 1
                && historyPager.Right >= footer.ClientSize.Width - footer.Padding.Right - 1,
                "The complete pagination group must align with the right edge.");
            SaveEditorScreenshot(this, "pagination", "history-147-reference-layout");
            sizeSelect.SelectedIndex = 1;
            Require(historyPager.PageSize == 20 && _historyGrid.Rows.Count == 20,
                "Separate page size selector did not update the bound page.");
            _pointsDeviceId = device.Id;
            BindTags(); _pointsDialog.Show(this);
            PerformLayoutRecursively(_pointsDialog); await Task.Delay(100);
            Pager(_tagGrid).Current = 3;
            Require(_tagGrid.Rows.Count == 3 && SelectedTag()?.Id == device.Tags[20].Id,
                "Point pagination lost the owning device or stable tag ID.");
            SaveEditorScreenshot(_pointsDialog, "pagination", "points-last-page");
            _pointsDialog.Hide(); _pointsDeviceId = null;

            ShowPage("logs"); _logTabs.SelectedIndex = 1;
            for (var i = 0; i < 23; i++) _log.Write(GatewayLogLevel.Error, $"pagination-event-{i:000}");
            _eventArchive.Keyword.Text = "pagination-event-";
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(100); await _eventArchive.QueryAsync();
                if (_eventArchive.Result?.Entries.Count == 23) break;
            }
            Require(_eventArchive.Result?.Entries.Count == 23, "Log fixture did not flush 23 records.");
            PerformLayoutRecursively(this); await Task.Delay(100);
            Pager(_eventArchive.Grid).Current = 3;
            Require(_eventArchive.Grid.Rows.Count == 3 && _eventArchive.Result!.Entries.Count == 23,
                "Log pagination changed the executed snapshot.");
            var directory = Path.GetDirectoryName(Path.GetFullPath(_smokeScreenshotPath ?? "ui-smoke.png"))!;
            var export = Path.Combine(directory, "pagination-events.jsonl");
            await _application.ExportLogQueryAsync(_eventArchive.Result!, export);
            var exportedMessages = new List<string>();
            foreach (var line in await File.ReadAllLinesAsync(export))
            {
                using var record = System.Text.Json.JsonDocument.Parse(line);
                if (record.RootElement.TryGetProperty("message", out var message)) exportedMessages.Add(message.GetString()!);
            }
            Require(exportedMessages.Order().SequenceEqual(Enumerable.Range(0, 23).Select(i => $"pagination-event-{i:000}")),
                "Log export omitted records from other pages.");
            SaveEditorScreenshot(this, "pagination", "logs-last-page");
            Console.WriteLine($"Pagination smoke passed: DeviceDpi={DeviceDpi}; page navigation, page size, stable device/tag IDs, final-page deletion, empty results, realtime refresh, complete history CSV and log JSONL.");
        }
        finally { await CommitConfigurationAsync(original, replaceCloudDraft: true); }
    }
}
