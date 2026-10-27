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
                if (navigationMenu.Collapsed) expandNavigationButton.PerformClick();
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    collapseNavigationButton.PerformClick();
                    await Task.Delay(80);
                    AssertNavigationShell(true, mode + " collapse " + cycle);
                    foreach (var key in _navigation.Keys)
                    {
                        _compactNavigation[key].PerformClick();
                        await Task.Yield();
                        if (_activePage != key) throw new InvalidOperationException("Collapsed route failed: " + key);
                        AssertNavigationShell(true, mode + "/" + key);
                    }
                    ShowPage("devices");
                    if (cycle == 2) SaveEditorScreenshot(this, "navigation-collapse", mode + "-collapsed");
                    expandNavigationButton.PerformClick();
                    await Task.Delay(80);
                    AssertNavigationShell(false, mode + " expand " + cycle);
                    if (_deviceSearch.Text != "Modbus") throw new InvalidOperationException("Navigation toggle lost device filters.");
                }
                SaveEditorScreenshot(this, "navigation-collapse", mode + "-expanded");
                for (var click = 0; click < 11; click++)
                    (navigationMenu.Collapsed ? expandNavigationButton : collapseNavigationButton).PerformClick();
                await Task.Delay(100);
                AssertNavigationShell(true, mode + " rapid clicks");
                _refreshTimer.Start(); await Task.Delay(1100); _refreshTimer.Stop();
                AssertNavigationShell(true, mode + " timer refresh");
                expandNavigationButton.PerformClick();
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
        if (navigationMenu.Collapsed != collapsed || compactNavigationBar.Visible != collapsed
            || navigationViewportPanel.Visible == collapsed)
            throw new InvalidOperationException(context + ": navigation display/state mismatch.");
        if (collapsed)
        {
            if (applicationShell.ColumnStyles[0].Width != 0 || applicationBody.Left != 0
                || applicationBody.Width != applicationShell.ClientSize.Width
                || applicationBody.Top != compactNavigationBar.Bottom
                || compactNavigationBar.Height != UiTheme.Scale(this, 52))
                throw new InvalidOperationException(context + ": collapsed toolbar does not release the full page width.");
            AssertVisibleBounds(compactApplicationIcon, context + " application logo");
            if (compactApplicationIcon.Image is null) throw new InvalidOperationException("Missing application logo.");
            var centerY = compactApplicationIcon.PointToScreen(new Point(0, compactApplicationIcon.Height / 2)).Y;
            var previousRight = compactApplicationIcon.RectangleToScreen(compactApplicationIcon.ClientRectangle).Right;
            foreach (var button in _compactNavigation.Values.Append(expandNavigationButton))
            {
                AssertVisibleBounds(button, context + " " + button.AccessibleName);
                var bounds = button.RectangleToScreen(button.ClientRectangle);
                if (bounds.Left < previousRight || Math.Abs(bounds.Top + bounds.Height / 2 - centerY) > 1)
                    throw new InvalidOperationException(context + ": icons and expand action are not in one horizontal row.");
                previousRight = bounds.Right;
            }
            if (_compactNavigation[_activePage].Type != AntdUI.TTypeMini.Primary)
                throw new InvalidOperationException(context + ": current route is not highlighted.");
        }
        else
        {
            var width = UiTheme.Scale(applicationShell, applicationShell.ClientSize.Width < UiTheme.Scale(applicationShell, 1000) ? 172 : 192);
            if (navigationViewportPanel.Width != width || applicationShell.ColumnStyles[0].Width != width
                || applicationBody.Left != navigationViewportPanel.Right || applicationBody.Top != 0
                || navigationContentPanel.Width != width || !navigationBrandPanel.Visible)
                throw new InvalidOperationException(context + ": expanded sidebar geometry mismatch.");
            foreach (var item in _navigation.Values) AssertNavigationItemFits(item, context);
            AssertVisibleBounds(collapseNavigationButton, context + " collapse button");
        }
    }
}
