namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeNavigationCollapseAsync()
    {
        if (!_smokeTest) throw new InvalidOperationException("Navigation checks require an isolated host.");
        var originalSize = ClientSize;
        try
        {
            ShowPage("devices");
            _deviceSearch.Text = "Modbus";
            foreach (var mode in new[] { "wide", "compact", "maximized" })
            {
                WindowState = mode == "maximized" ? FormWindowState.Maximized : FormWindowState.Normal;
                if (mode != "maximized") ClientSize = new Size(UiTheme.Scale(this, mode == "wide" ? 1400 : 980), UiTheme.Scale(this, 680));
                await Task.Delay(150);
                if (navigationMenu.Collapsed) collapseNavigationButton.PerformClick();
                await Task.Delay(80);
                AssertNavigationShell(false, mode + " initial");
                await MeasureNavigationResponseAsync(mode);
                ShowPage("devices");
                var stableIcons = CaptureNavigationIcons();
                var stableLogo = navigationApplicationIcon.Bounds;
                var stableToggle = collapseNavigationButton.Bounds;
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    collapseNavigationButton.PerformClick();
                    await Task.Delay(80);
                    AssertNavigationShell(true, mode + " collapse " + cycle);
                    AssertNavigationPositions(stableIcons, stableLogo, stableToggle, mode);
                    foreach (var key in _navigation.Keys)
                    {
                        var hit = _navigation[key].Rect("Icon", 0, navigationMenu.ScrollBar.ValueY);
                        var point = new IntPtr(((hit.Top + hit.Height / 2) << 16) | (hit.Left + hit.Width / 2));
                        SendControlSmokeMessage(navigationMenu.Handle, 0x0201, new IntPtr(1), point);
                        SendControlSmokeMessage(navigationMenu.Handle, 0x0202, IntPtr.Zero, point);
                        await Task.Yield();
                        if (_activePage != key) throw new InvalidOperationException("Collapsed route failed: " + key);
                        AssertNavigationShell(true, mode + "/" + key);
                    }
                    ShowPage("devices");
                    if (cycle == 2) SaveEditorScreenshot(this, "navigation-collapse", mode + "-collapsed");
                    collapseNavigationButton.PerformClick();
                    await Task.Delay(80);
                    AssertNavigationShell(false, mode + " expand " + cycle);
                    AssertNavigationPositions(stableIcons, stableLogo, stableToggle, mode);
                    if (_deviceSearch.Text != "Modbus") throw new InvalidOperationException("Navigation toggle lost device filters.");
                }
                SaveEditorScreenshot(this, "navigation-collapse", mode + "-expanded");
                if (mode == "wide")
                {
                    foreach (var name in new[] { "ProbeDevice", "AddDevice" })
                    {
                        var button = Descendants(_pages["devices"]).OfType<GatewayButton>().Single(control => control.Name == name);
                        typeof(Control).GetMethod("OnMouseEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(button, [EventArgs.Empty]);
                        await Task.Delay(250);
                        SaveEditorScreenshot(this, "navigation-hover", name);
                        typeof(Control).GetMethod("OnMouseLeave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(button, [EventArgs.Empty]);
                        await Task.Delay(250);
                    }
                }
                for (var click = 0; click < 11; click++) collapseNavigationButton.PerformClick();
                await Task.Delay(100);
                AssertNavigationShell(true, mode + " rapid clicks");
                _refreshTimer.Start(); await Task.Delay(1100); _refreshTimer.Stop();
                AssertNavigationShell(true, mode + " timer refresh");
                AssertNavigationPositions(stableIcons, stableLogo, stableToggle, mode);
                collapseNavigationButton.PerformClick();
                await Task.Delay(80);
                AssertNavigationShell(false, mode + " final expand");
                Console.WriteLine("Stable navigation collapse: " + mode);
            }
            WindowState = FormWindowState.Normal;
            ClientSize = originalSize;
            ShowPage("overview");
            RefreshServerResources(await _application.GetServerResourcesAsync());
            _refreshTimer.Start();
            try { await MeasureNavigationResponseAsync("live", overviewOnly: true); }
            finally { _refreshTimer.Stop(); }
            Console.WriteLine($"Navigation collapse smoke passed: wide/compact/maximized windows, repeated and rapid toggles, eight routes, live resource/timer refresh, unchanged icon/logo/toggle positions, no overview horizontal overflow, bounded click latency and preserved filters at actual {DeviceDpi} DPI; no forced layout from the test.");
        }
        finally { _refreshTimer.Stop(); WindowState = FormWindowState.Normal; ClientSize = originalSize; }
    }

    private async Task MeasureNavigationResponseAsync(string mode, bool overviewOnly = false)
    {
        // Diagnostic only: measure the real click path without forcing layout or paint.
        // Queue latency is not a frame/presentation time on an isolated desktop.
        foreach (var key in _navigation.Keys.Where(key => !overviewOnly || key == "overview"))
        {
            ShowPage(key);
            await Task.Delay(150);
            var controls = Descendants(this).Prepend(this).ToArray();
            var layouts = new Dictionary<Control, int>();
            void CountLayout(object? sender, LayoutEventArgs args)
            {
                if (sender is Control control) layouts[control] = layouts.GetValueOrDefault(control) + 1;
            }
            foreach (var control in controls) control.Layout += CountLayout;
            try
            {
                for (var click = 0; click < 4; click++)
                {
                    layouts.Clear();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    collapseNavigationButton.PerformClick();
                    var clickMs = timer.Elapsed.TotalMilliseconds;
                    var queued = new TaskCompletionSource<double>();
                    BeginInvoke((Action)(() => queued.SetResult(timer.Elapsed.TotalMilliseconds)));
                    var dispatchMs = await queued.Task;
                    await Task.Delay(100);
                    AssertNavigationShell(click % 2 == 0, mode + "/" + key + " measured toggle");
                    var busiest = string.Join(";", layouts.OrderByDescending(pair => pair.Value).Take(5)
                        .Select(pair => $"{pair.Key.Name}/{pair.Key.GetType().Name}:{pair.Value}"));
                    Console.WriteLine(FormattableString.Invariant($"Navigation timing: mode={mode} page={key} toggle={click + 1} collapsed={navigationMenu.Collapsed} clickMs={clickMs:F2} dispatchMs={dispatchMs:F2} layoutEvents={layouts.Values.Sum()} busiest={busiest}"));
                    if (key == "overview")
                    {
                        var viewport = _overviewPageView.viewport;
                        Console.WriteLine($"Overview bounds: viewport={viewport.ClientSize} content={_overviewPageView.dashboardContent.Bounds} horizontal={viewport.HorizontalScroll.Visible} extent={viewport.AutoScrollMinSize} scroll={viewport.AutoScrollPosition}");
                        if (viewport.HorizontalScroll.Visible || _overviewPageView.dashboardContent.Width > viewport.ClientSize.Width)
                            throw new InvalidOperationException("Overview leaves horizontal overflow after navigation toggle.");
                        // A regression guard against the reported half-second UI stalls,
                        // not a claim about frame rate or the display compositor.
                        if (clickMs > 250 || dispatchMs > 250)
                            throw new InvalidOperationException($"Overview navigation stalled: click={clickMs:F2}ms queue={dispatchMs:F2}ms.");
                    }
                    if (key == "overview" && click < 2)
                        SaveEditorScreenshot(this, "navigation-overview", mode + (navigationMenu.Collapsed ? "-collapsed" : "-expanded"));
                }
            }
            finally { foreach (var control in controls) control.Layout -= CountLayout; }
        }
    }

    private void AssertNavigationShell(bool collapsed, string context)
    {
        var width = UiTheme.Scale(applicationShell, collapsed ? 56 : 184);
        if (navigationMenu.Collapsed != collapsed || navigationViewportPanel.Width != width
            || applicationShell.ColumnStyles[0].Width != width || applicationBody.Left != navigationViewportPanel.Right
            || navigationContentPanel.Width != width || navigationBrandPanel.Visible == collapsed)
            throw new InvalidOperationException($"{context}: menu/shell states disagree: collapsed={navigationMenu.Collapsed}; expectedWidth={width}; rail={navigationViewportPanel.Width}; content={navigationContentPanel.Width}; column={applicationShell.ColumnStyles[0].Width}; bodyLeft={applicationBody.Left}; brand={navigationBrandPanel.Visible}.");
        foreach (var item in _navigation.Values)
        {
            AssertNavigationItemFits(item, context);
            if (!collapsed) continue;
            var icon = item.Rect("Icon", 0, navigationMenu.ScrollBar.ValueY);
            if (Math.Abs(icon.Left + icon.Width / 2 - navigationMenu.ClientSize.Width / 2) > UiTheme.Scale(navigationMenu, 2))
                throw new InvalidOperationException(context + ": collapsed icon is not centered: " + item.Text);
        }
        AssertVisibleBounds(collapseNavigationButton, context + " toggle button");
        AssertVisibleBounds(navigationApplicationIcon, context + " application icon");
        if (navigationApplicationIcon.Image is null) throw new InvalidOperationException("Missing application icon.");
        var toggleCenter = collapseNavigationButton.PointToScreen(new Point(collapseNavigationButton.Width / 2, 0)).X;
        var logoCenter = navigationApplicationIcon.PointToScreen(new Point(navigationApplicationIcon.Width / 2, 0)).X;
        foreach (var bounds in CaptureNavigationIcons().Values)
            if (Math.Abs(bounds.Left + bounds.Width / 2 - PointToClient(new Point(toggleCenter, 0)).X) > 1
                || Math.Abs(toggleCenter - logoCenter) > 1)
                throw new InvalidOperationException(context + ": logo/toggle/menu icon columns do not align.");
    }
    private Dictionary<string, Rectangle> CaptureNavigationIcons() => _navigation.ToDictionary(pair => pair.Key, pair =>
    {
        var rect = pair.Value.Rect("Icon", 0, navigationMenu.ScrollBar.ValueY);
        return new Rectangle(PointToClient(navigationMenu.PointToScreen(rect.Location)), rect.Size);
    });

    private void AssertNavigationPositions(Dictionary<string, Rectangle> icons, Rectangle logo, Rectangle toggle, string context)
    {
        foreach (var (key, bounds) in CaptureNavigationIcons())
            if (bounds != icons[key])
                throw new InvalidOperationException($"{context}: {key} icon moved: {icons[key]} -> {bounds}.");
        if (navigationApplicationIcon.Bounds != logo || collapseNavigationButton.Bounds != toggle)
            throw new InvalidOperationException(context + ": logo or toggle moved during collapse.");
    }

}
