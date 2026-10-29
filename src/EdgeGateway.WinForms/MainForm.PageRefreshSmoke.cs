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
            GridPagination.GetPager(_realtimeGrid).PageSize = 50;
            PerformLayoutRecursively(this);
            AssertNavigationShell(false, "initial page layout without toggling");
            VerifyTableColumnDrag(_realtimeGrid);
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
                throw new InvalidOperationException($"Realtime polling rebuilt rows, lost reading position or failed to update values (source changes={resets}; row same={ReferenceEquals(selectedRow, _realtimeGrid.Rows[25])}; before={state}; after={GridViewState.Capture(_realtimeGrid, row => ((SampleRow)row).Sample.TagId)}; value={_realtimeGrid.Rows[25].Cells["Value"].Value}).");
            _realtimeSearch.Text = "点位 001";
            RefreshRealtime(samples);
            if (_realtimeGrid.Rows.Count != 1 || ((SampleRow)_realtimeGrid.Rows[0].DataBoundItem!).Sample.TagId != device.Tags[0].Id)
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
                    AssertHorizontalFilters((Control?)tabs?.SelectedTab ?? _pages[key]);
                    AssertPageLayoutDensity((Control?)tabs?.SelectedTab ?? _pages[key]);
                    // AntdUI keeps unselected page controls in the tree before their first layout.
                    // Visit each selected page, rather than measuring an unopened page at width zero.
                    foreach (var toggle in Descendants((Control?)tabs?.SelectedTab ?? _pages[key]).OfType<GatewaySwitch>().Where(toggle => toggle.Visible))
                    {
                        if (toggle.Width != UiTheme.Scale(toggle, 52) || toggle.Height < UiTheme.Scale(toggle, 30))
                            throw new InvalidOperationException($"Switch collapsed below its usable bounds: {toggle.Name} {toggle.Bounds} dpi={toggle.DeviceDpi}");
                        if (toggle.Name == "CloudField_autoStart")
                        {
                            var saved = toggle.Checked;
                            try
                            {
                                FocusForSmoke(toggle, toggle, this, "auto-start switch");
                                SendControlSmokeMessage(toggle.Handle, 0x0100, new IntPtr((int)Keys.Space), IntPtr.Zero);
                                SendControlSmokeMessage(toggle.Handle, 0x0101, new IntPtr((int)Keys.Space), IntPtr.Zero);
                                if (toggle.Checked == saved) throw new InvalidOperationException("Auto-start switch did not respond to Space.");
                            }
                            finally { toggle.Checked = saved; }
                        }
                    }
                    await VerifyPageRefreshStabilityAsync(_pages[key], key + "/" + index);
                    var wideSize = Size;
                    try
                    {
                        Size = new Size(MinimumSize.Width, wideSize.Height);
                        await Task.Yield();
                        PerformLayoutRecursively(this);
                        AssertNavigationShell(false, "narrow page layout without toggling");
                        var selectedPage = (Control?)tabs?.SelectedTab ?? _pages[key];
                        AssertHorizontalFilters(selectedPage);
                        AssertPageToolbarsFit(selectedPage, "narrow filters " + key + "/" + index);
                        AssertPageLayoutDensity(selectedPage);
                        SaveEditorScreenshot(this, "filters-narrow", key + "-" + index);
                    }
                    finally { Size = wideSize; PerformLayoutRecursively(this); }
                    AssertPageLayoutDensity((Control?)tabs?.SelectedTab ?? _pages[key]);
                }
                SaveEditorScreenshot(this, "page-refresh", key);
            }
            Console.WriteLine($"Page refresh smoke passed: eight pages and every database/platform/log tab; stable data sources, control bounds and scroll during timer refresh; realtime unchanged/changed samples retained rows and selection, filter updated membership, and 40 real simulator points refreshed at actual {DeviceDpi} DPI. MQTT disabled; no external databases or hardware.");
        }
        finally { _refreshTimer.Stop(); await _application.StopAsync(); await RestoreConfigurationForSmokeAsync(original); }
    }

    private static void AssertPageLayoutDensity(Control page)
    {
        foreach (var toolbar in Descendants(page).OfType<WrappingToolbar>().Where(control => control.Visible))
        {
            var children = toolbar.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
            if (children.Length == 0) continue;
            var contentBottom = children.Max(control => control.Bottom + control.Margin.Bottom) + toolbar.Padding.Bottom;
            if (toolbar.ClientSize.Height - contentBottom > UiTheme.Scale(toolbar, 8))
                throw new InvalidOperationException($"Unused toolbar height: {toolbar.Name}, height={toolbar.Height}, content bottom={contentBottom}.");
            if (toolbar.Parent is TableLayoutPanel { Name: "DeviceFiltersHost" } host
                && host.Height - host.Padding.Vertical - toolbar.Height > UiTheme.Scale(host, 8))
                throw new InvalidOperationException($"Unused filter host height: {host.Height}; filters={toolbar.Height}.");
        }
        foreach (var grid in Descendants(page).OfType<GatewayTable>().Where(control => control.Visible && control.Rows.Count > 0))
        {
            var columns = grid.Columns.Where(column => column.Visible).ToArray();
            var viewport = grid.ClientSize.Width - (grid.ScrollBar.ShowY ? grid.ScrollBar.SIZE : 0);
            if (grid.AutoSizeColumnsMode == DataGridViewAutoSizeColumnsMode.Fill
                && columns.Sum(column => column.WidthPixel) < viewport - 2)
                throw new InvalidOperationException($"Unused table width: {grid.Name}; columns={columns.Sum(column => column.WidthPixel)}; viewport={viewport}.");
            if (grid.Name == "TelemetryDiagnostics" && columns.Sum(column => UiTheme.Scale(grid, column.LogicalMinimum)) <= viewport
                && grid.ScrollBar.ShowX)
                throw new InvalidOperationException("Diagnostics table has unnecessary horizontal scrolling.");
            foreach (var column in columns)
                if (column.WidthPixel < UiTheme.Scale(grid, column.LogicalMinimum) - 2)
                    throw new InvalidOperationException($"Column below readable minimum: {grid.Name}/{column.Name}; actual={column.WidthPixel}; minimum={UiTheme.Scale(grid, column.LogicalMinimum)}.");
        }
    }

    private static void VerifyTableColumnDrag(GatewayTable grid)
    {
        var viewportWidth = grid.ClientSize.Width - (grid.ScrollBar.ShowY ? grid.ScrollBar.SIZE : 0);
        var widths = grid.Columns.Where(column => column.Visible).Sum(column => column.WidthPixel);
        if (!grid.EnableHeaderResizing || widths < viewportWidth - 2)
            throw new InvalidOperationException($"Table does not fill its viewport: {grid.Name}, columns={widths}, viewport={viewportWidth}.");
        var column = grid.Columns.First(column => column.Visible && !column.Frozen);
        var header = grid.GetCellDisplayRectangle(column.Index, -1, false);
        var before = column.WidthPixel;
        var x = header.Right - 1;
        var y = header.Top + header.Height / 2;
        var delta = UiTheme.Scale(grid, 36);
        void Mouse(string method, MouseButtons buttons, int position)
        {
            typeof(AntdUI.Table).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(grid, [new MouseEventArgs(buttons, 1, position, y, 0)]);
        }
        Mouse("OnMouseMove", MouseButtons.None, x);
        Mouse("OnMouseDown", MouseButtons.Left, x);
        Mouse("OnMouseMove", MouseButtons.Left, x + delta);
        Mouse("OnMouseUp", MouseButtons.Left, x + delta);
        if (column.WidthPixel < before + delta - 2)
            throw new InvalidOperationException($"Official header drag failed: {before} -> {column.WidthPixel}.");
        var dragged = column.WidthPixel;
        grid.RepaintRows();
        var pager = GridPagination.GetPager(grid);
        var savedSize = pager.PageSize;
        pager.PageSize = 10;
        pager.Current = 2;
        pager.Current = 1;
        pager.PageSize = savedSize;
        if (column.WidthPixel != dragged)
            throw new InvalidOperationException("Column drag width was lost during refresh or paging.");
        Console.WriteLine($"Table column resize passed: {grid.Name}; {before} -> {dragged}; official drag, refresh and paging retained width.");
    }

    private async Task VerifyPageRefreshStabilityAsync(Control page, string context)
    {
        var controls = Descendants(page).Prepend(page).Where(control => control.Visible
            && control is not Label && (control is TableLayoutPanel or GatewayTable or TabControl or Button or TextBox or ComboBox)).ToArray();
        var bounds = controls.ToDictionary(control => control, control => control.Bounds);
        var scrolls = Descendants(page).OfType<ScrollableControl>().Where(scroll => scroll.Visible && scroll.AutoScroll)
            .ToDictionary(scroll => scroll, scroll => scroll.AutoScrollPosition);
        var grids = Descendants(page).OfType<GatewayTable>().ToArray();
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
