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
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    collapseNavigationButton.PerformClick();
                    await Task.Delay(80);
                    AssertNavigationShell(true, mode + " collapse " + cycle);
                    foreach (var key in _navigation.Keys)
                    {
                        navigationMenu.Select(_navigation[key]);
                        await Task.Yield();
                        if (_activePage != key) throw new InvalidOperationException("Collapsed route failed: " + key);
                        AssertNavigationShell(true, mode + "/" + key);
                    }
                    ShowPage("devices");
                    if (cycle == 2) SaveEditorScreenshot(this, "navigation-collapse", mode + "-collapsed");
                    collapseNavigationButton.PerformClick();
                    await Task.Delay(80);
                    AssertNavigationShell(false, mode + " expand " + cycle);
                    if (_deviceSearch.Text != "Modbus") throw new InvalidOperationException("Navigation toggle lost device filters.");
                }
                SaveEditorScreenshot(this, "navigation-collapse", mode + "-expanded");
                for (var click = 0; click < 11; click++) collapseNavigationButton.PerformClick();
                await Task.Delay(100);
                AssertNavigationShell(true, mode + " rapid clicks");
                _refreshTimer.Start(); await Task.Delay(1100); _refreshTimer.Stop();
                AssertNavigationShell(true, mode + " timer refresh");
                collapseNavigationButton.PerformClick();
                await Task.Delay(80);
                AssertNavigationShell(false, mode + " final expand");
                Console.WriteLine("Stable navigation collapse: " + mode);
            }
            Console.WriteLine($"Navigation collapse smoke passed: wide/compact/maximized windows, repeated and rapid toggles, eight routes, timer refresh, actual sidebar/body/menu geometry and preserved filters at actual {DeviceDpi} DPI; no forced layout from the test.");
        }
        finally { _refreshTimer.Stop(); WindowState = FormWindowState.Normal; ClientSize = originalSize; }
    }

    private void AssertNavigationShell(bool collapsed, string context)
    {
        var width = UiTheme.Scale(applicationShell, collapsed ? 56 : applicationShell.ClientSize.Width < UiTheme.Scale(applicationShell, 1000) ? 172 : 192);
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
    }
}
