using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokePageRefreshAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("Page refresh requires an isolated stopped host.");
        var original = Clone(_configuration);
        var device = new DeviceConfiguration
        {
            Name = "连续刷新验证", DriverId = "simulator", PollIntervalMs = 100,
            Tags = Enumerable.Range(1, 40).Select(index => new TagConfiguration
                { Name = $"点位 {index:000}", Address = "constant:" + index, DataType = TagDataType.Float64 }).ToList()
        };
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt = new MqttConfiguration { Enabled = false };
            fixture.Logging.LogAcquisitionSamples = false;
            fixture.Devices = [device]; fixture.Channels = []; fixture.Databases = []; fixture.DatabaseWriteGroups = [];
            await CommitConfigurationAsync(fixture, device.Id, replaceCloudDraft: true);
            ShowPage("realtime");
            var timestamp = DateTimeOffset.UtcNow;
            var samples = device.Tags.Select((tag, index) => new TelemetrySample(device.Id, device.Name, tag.Id, tag.Name,
                tag.Address, (double)index, (double)index, SampleQuality.Good, timestamp, tag.Unit)).ToArray();
            RefreshRealtime(samples);
            _realtimeGrid.Parent!.Controls.OfType<AntdUI.Pagination>().Single().PageSize = 50;
            PerformLayoutRecursively(this);
            _realtimeGrid.CurrentCell = _realtimeGrid.Rows[25].Cells["Error"];
            _realtimeGrid.FirstDisplayedScrollingRowIndex = 20;
            var state = GridViewState.Capture(_realtimeGrid, row => ((SampleRow)row).Sample.TagId);
            var source = _realtimeGrid.DataSource; var selectedRow = _realtimeGrid.Rows[25];
            var resets = 0;
            void SourceChanged(object? sender, EventArgs e) => resets++;
            _realtimeGrid.DataSourceChanged += SourceChanged;
            try
            {
                RefreshRealtime(samples);
                RefreshRealtime(samples.Select(sample => sample with { Value = 42D, Timestamp = timestamp.AddSeconds(1) }).ToArray());
            }
            finally { _realtimeGrid.DataSourceChanged -= SourceChanged; }
            if (resets != 0 || !ReferenceEquals(source, _realtimeGrid.DataSource) || !ReferenceEquals(selectedRow, _realtimeGrid.Rows[25])
                || state != GridViewState.Capture(_realtimeGrid, row => ((SampleRow)row).Sample.TagId)
                || !Equals(_realtimeGrid.Rows[25].Cells["Value"].Value, "42"))
                throw new InvalidOperationException($"Realtime polling rebuilt rows, lost reading position or failed to update values (source changes={resets}).");
            _realtimeSearch.Text = "点位 001";
            RefreshRealtime(samples);
            if (_realtimeGrid.Rows.Count != 1 || ((SampleRow)_realtimeGrid.Rows[0].DataBoundItem).Sample.TagId != device.Tags[0].Id)
                throw new InvalidOperationException("Realtime filter did not update row structure.");
            _realtimeSearch.Clear();
            await _application.StartDeviceAsync(device.Id);
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (_application.GetLatest().Count != 40 && wait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            if (_application.GetLatest().Count != 40) throw new InvalidOperationException("Isolated simulator did not publish 40 points.");
            foreach (var key in _pages.Keys)
            {
                ShowPage(key);
                var tabs = key switch { "database" => _databaseTabs, "cloud" => _cloudTabs, "logs" => _logTabs, _ => null };
                for (var index = 0; index < (tabs?.TabPages.Count ?? 1); index++)
                {
                    if (tabs is not null) tabs.SelectedIndex = index;
                    await RefreshSnapshotsAsync();
                    if (_serverResourceRefresh is not null) await _serverResourceRefresh;
                    await Task.Delay(120);
                    PerformLayoutRecursively(this);
                    await VerifyPageRefreshStabilityAsync(_pages[key], key + "/" + index);
                }
                SaveEditorScreenshot(this, "page-refresh", key);
            }
            Console.WriteLine($"Page refresh smoke passed: eight pages and every database/platform/log tab; stable data sources, control bounds and scroll during timer refresh; realtime unchanged/changed samples retained rows and selection, filter updated membership, and 40 real simulator points refreshed at actual {DeviceDpi} DPI. MQTT disabled; no external databases or hardware.");
        }
        finally { _refreshTimer.Stop(); await _application.StopAsync(); await RestoreConfigurationForSmokeAsync(original); }
    }

    private async Task VerifyPageRefreshStabilityAsync(Control page, string context)
    {
        var controls = Descendants(page).Prepend(page).Where(control => control.Visible
            && control is not Label && (control is TableLayoutPanel or DataGridView or TabControl or Button or TextBox or ComboBox)).ToArray();
        var bounds = controls.ToDictionary(control => control, control => control.Bounds);
        var scrolls = Descendants(page).OfType<ScrollableControl>().Where(scroll => scroll.Visible && scroll.AutoScroll)
            .ToDictionary(scroll => scroll, scroll => scroll.AutoScrollPosition);
        var grids = Descendants(page).OfType<DataGridView>().ToArray();
        var failures = new HashSet<string>();
        void Changed(object? sender, EventArgs e)
        {
            if (sender is Control control && control.Bounds != bounds[control]) failures.Add("bounds moved: " + control.Name);
        }
        void Rebound(object? sender, EventArgs e) => failures.Add("data source rebound: " + ((Control)sender!).Name);
        foreach (var control in controls) { control.LocationChanged += Changed; control.SizeChanged += Changed; }
        foreach (var grid in grids) grid.DataSourceChanged += Rebound;
        try
        {
            _refreshTimer.Start();
            await Task.Delay(2200);
            _refreshTimer.Stop();
            if (_serverResourceRefresh is not null) await _serverResourceRefresh;
            foreach (var (scroll, before) in scrolls)
                if (scroll.AutoScrollPosition != before) failures.Add("scroll moved: " + scroll.Name);
            if (failures.Count != 0) throw new InvalidOperationException(context + ": " + string.Join(", ", failures));
            Console.WriteLine("Stable page refresh: " + context);
        }
        finally
        {
            _refreshTimer.Stop();
            foreach (var control in controls) { control.LocationChanged -= Changed; control.SizeChanged -= Changed; }
            foreach (var grid in grids) grid.DataSourceChanged -= Rebound;
        }
    }
}
