using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool LiveLogsSmoke { get; set; }

    private async Task SmokeLiveLogsAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("Live logs require an isolated stopped host.");
        var original = Clone(_configuration);
        var originalSize = Size;
        var view = _eventArchive;
        var grid = view.Grid;
        const string prefix = "live-log-check-";
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        async Task Until(Func<bool> condition, string context)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(100);
            Require(condition(), context + ": " + view.State.Text);
        }
        string MessageAt(int index) => grid.Rows[index].Cells["Message"].Value?.ToString() ?? "";
        try
        {
            var settings = CaptureLoggingSettings();
            settings.MinimumLevel = GatewayLogLevel.Debug;
            settings.LogAcquisitionSamples = true;
            CompleteConfigurationCommit(await _application.ApplyLoggingSettingsAsync(settings));
            ShowPage("logs");
            Require(_logTabs.SelectedTab == view.Page, "Running logs must be the initial tab.");
            Require(_logTabs.TabPages.Cast<TabPage>().Select(tab => tab.Text).SequenceEqual(new[] { "运行日志", "采样数据", "日志设置" }), "Wrong log tab order.");
            view.Keyword.Text = prefix;
            for (var i = 0; i < 60; i++) _log.Write((GatewayLogLevel)(i % 4), prefix + i.ToString("000"));
            _refreshTimer.Start();
            await Until(() => view.Result?.Entries.Count == 60, "Timer did not load appended logs");
            Require(grid.Rows.Count == 60 && !Descendants(view.Page).OfType<AntdUI.Pagination>().Any(pager => pager.Visible), "Running logs still paginate.");
            var colors = grid.Rows.Select(row => ((AntdUI.CellText)row.Cells["Level"].Presentation()!).Fore).Distinct().ToArray();
            Require(colors.Length == 4 && colors.All(color => color.HasValue), "Four log levels do not have distinct colors.");
            SaveEditorScreenshot(this, "live-logs", "four-levels");

            grid.CurrentCell = grid.Rows[30].Cells["Message"];
            grid.FirstDisplayedScrollingRowIndex = 25;
            var selected = grid.CurrentCell.Value?.ToString();
            var first = MessageAt(grid.FirstDisplayedScrollingRowIndex);
            _log.Write(GatewayLogLevel.Warning, prefix + "new-record");
            await Until(() => view.Result?.Entries.Count == 61, "New event did not arrive");
            Require(grid.CurrentCell?.Value?.ToString() == selected, "Refresh lost selection.");
            Require(MessageAt(grid.FirstDisplayedScrollingRowIndex) == first, "Refresh moved the reader's first visible record.");
            SaveEditorScreenshot(this, "live-logs", "reading-position");

            view.Live.PerformClick();
            var paused = view.Result;
            _log.Write(GatewayLogLevel.Error, prefix + "paused-record");
            await Task.Delay(2500);
            Require(ReferenceEquals(paused, view.Result), "Paused view refreshed.");
            view.Live.PerformClick();
            await Until(() => view.Result?.Entries.Count == 62, "Resume did not catch up");
            view.Level.SelectedItem = GatewayLogLevel.Error;
            await Until(() => view.Result?.Entries.Count > 0 && view.Result.Entries.All(entry => entry.Level == GatewayLogLevel.Error), "Level filter failed");
            view.Level.SelectedItem = GatewayLogLevel.Debug;
            view.Limit.Value = 12;
            await Until(() => view.Result?.Query.Limit == 12 && grid.Rows.Count == 12, "Row cap failed");
            grid.VerticalScrollingOffset = 0;
            _log.Write(GatewayLogLevel.Information, prefix + "latest");
            await Until(() => view.Result?.Entries[0].Message == prefix + "latest", "Top did not follow latest");
            Require(grid.VerticalScrollingOffset == 0, "Latest view scrolled away from top.");

            var snapshot = view.Result!;
            var directory = Path.GetDirectoryName(Path.GetFullPath(_smokeScreenshotPath!))!;
            _log.Write(GatewayLogLevel.Error, prefix + "after-snapshot");
            await Until(() => view.Result?.Entries[0].Message == prefix + "after-snapshot", "Snapshot fixture did not advance");
            var exportPath = Path.Combine(directory, "frozen-events.jsonl");
            await _application.ExportLogQueryAsync(snapshot, exportPath);
            var exportText = await File.ReadAllTextAsync(exportPath);
            Require(!exportText.Contains("after-snapshot") && exportText.Contains(prefix + "latest"), "Export did not preserve its immutable snapshot.");

            view.From.Value = DateTime.Now.AddDays(-2);
            Require(view.Live.Text == "实时刷新", "Editing the date did not pause live refresh.");
            view.To.Value = DateTime.Now.AddDays(-1);
            await view.QueryAsync();
            Require(grid.Rows.Count == 0, "Historical empty query retained current rows.");
            view.Live.PerformClick();
            await Until(() => grid.Rows.Count == 12, "Could not return from history to live logs");

            _logTabs.SelectedTab = _sampleArchive.Page;
            var hidden = view.Result;
            _log.Write(GatewayLogLevel.Error, prefix + "hidden");
            await Task.Delay(2500);
            Require(ReferenceEquals(hidden, view.Result), "Hidden event page still queried.");
            var sampleLog = (IConfigurableGatewayLog)_log;
            for (var i = 0; i < 23; i++) sampleLog.WriteSample(new TelemetrySample(Guid.NewGuid(), "日志分页夹具", Guid.NewGuid(), "点位" + i, "test", i, i, SampleQuality.Good, DateTimeOffset.Now, ""));
            _sampleArchive.Device.Text = "日志分页夹具";
            for (var i = 0; i < 30 && _sampleArchive.Result?.Entries.Count != 23; i++) { await Task.Delay(100); await _sampleArchive.QueryAsync(); }
            Require(_sampleArchive.Result?.Entries.Count == 23, "Sample file fixture did not flush.");
            var pager = GridPagination.GetPager(_sampleArchive.Grid);
            pager.Current = 3;
            Require(_sampleArchive.Grid.Rows.Count == 3 && pager.Visible, "Samples lost pagination.");
            SaveEditorScreenshot(this, "live-logs", "samples-page-three");
            _logTabs.SelectedTab = view.Page;
            ClientSize = new Size(UiTheme.Scale(this, 1000), UiTheme.Scale(this, 680));
            await Until(() => view.Result?.Entries[0].Message == prefix + "hidden", "Return to visible event page did not refresh");
            await Task.Delay(2500);
            Require(view.Live.Text == "暂停刷新", "Tab or window layout changes paused live refresh.");
            AssertPageToolbarsFit(view.Page, "live logs narrow");
            SaveEditorScreenshot(this, "live-logs", "narrow");
            _logTabs.SelectedTab = _loggingPageView.SettingsTab;
            BringIntoViewAndAssert(_loggingApply, "last settings tab");
            SaveEditorScreenshot(this, "live-logs", "settings-last");
            Console.WriteLine($"Live logs smoke passed: actual timer/file append, four colors, unpaged rows, selection/scroll, pause/resume, filters/cap, frozen export, history/visibility, sample pagination and settings-last order; DeviceDpi={DeviceDpi}.");
        }
        finally
        {
            _refreshTimer.Stop();
            await _application.ApplyLoggingSettingsAsync(original.Logging);
            Size = originalSize;
        }
    }
}
