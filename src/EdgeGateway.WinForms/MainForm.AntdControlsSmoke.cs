using System.Runtime.InteropServices;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private void AssertNavigationItemFits(AntdUI.MenuItem item, string context)
    {
        var scroll = navigationMenu.ScrollBar.ValueY;
        var bounds = item.Rect(0, scroll);
        var icon = item.Rect("Icon", 0, scroll);
        if (bounds.Width <= 0 || !navigationMenu.ClientRectangle.Contains(bounds) || !bounds.Contains(icon))
            throw new InvalidOperationException(context + ": navigation item or icon is clipped: " + item.Text);
        if (navigationMenu.Collapsed) return;
        var text = item.Rect("Text", 0, scroll);
        var required = TextRenderer.MeasureText(item.Text, navigationMenu.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (!bounds.Contains(text) || text.Width < required.Width || text.Height < required.Height)
            throw new InvalidOperationException(context + ": navigation text is clipped: " + item.Text);
    }
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostControlSmokeMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendControlSmokeMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential)]
    private struct ControlSmokeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out ControlSmokeRect rect);

    private async Task SmokeInitialOverviewScrollAsync()
    {
        var viewport = _overviewPageView.viewport;
        PerformLayoutRecursively(this);
        var originalSize = ClientSize;
        try
        {
            if (!viewport.VerticalScroll.Visible)
            {
                ClientSize = new Size(ClientSize.Width, UiTheme.Scale(this, 600));
                PerformLayoutRecursively(this);
            }
            var point = viewport.PointToScreen(new Point(viewport.Width / 2, viewport.Height / 2));
            var packedPoint = new IntPtr((point.Y << 16) | (point.X & 0xffff));
            if (!PostControlSmokeMessage(_overviewDeviceSummary.Handle, 0x020A, new IntPtr(unchecked(-120 << 16)), packedPoint))
                throw new InvalidOperationException("Cannot post initial dashboard wheel input.");
            await Task.Delay(150);
            if (viewport.AutoScrollPosition.Y >= 0) throw new InvalidOperationException("First-display dashboard wheel did not scroll.");
            var wheelPosition = viewport.AutoScrollPosition.Y;
            await RefreshSnapshotsAsync();
            PerformLayoutRecursively(this);
            if (viewport.AutoScrollPosition.Y != wheelPosition) throw new InvalidOperationException("Dashboard refresh reset the wheel position.");
            SendControlSmokeMessage(viewport.Handle, 0x0115, new IntPtr(7), IntPtr.Zero); // SB_BOTTOM
            var bottom = viewport.AutoScrollPosition.Y;
            if (bottom >= 0) throw new InvalidOperationException("Dashboard scrollbar did not reach a lower position.");
            await RefreshSnapshotsAsync();
            PerformLayoutRecursively(this);
            if (viewport.AutoScrollPosition.Y != bottom) throw new InvalidOperationException("Dashboard refresh reset the scrollbar position.");
            SaveEditorScreenshot(this, "antd", "overview-scrolled");
            Console.WriteLine($"Initial overview scroll passed at {DeviceDpi} DPI: wheel over content, native scrollbar, position retained across resource refresh.");
        }
        finally { ClientSize = originalSize; viewport.AutoScrollPosition = Point.Empty; PerformLayoutRecursively(this); }
    }

    private async Task SmokeAntdNavigationAndSelectAsync()
    {
        var originalPage = _activePage;
        var width = navigationViewportPanel.Width;
        try
        {
            collapseNavigationButton.PerformClick();
            await Task.Delay(80);
            AssertNavigationShell(true, "horizontal menu");
            foreach (var key in _navigation.Keys)
            {
                _compactNavigation[key].PerformClick();
                await Task.Yield();
                if (_activePage != key)
                    throw new InvalidOperationException("Collapsed menu navigation is unreachable: " + key);
                AssertNavigationShell(true, "horizontal route " + key);
            }
            ShowPage("devices");
            SaveEditorScreenshot(this, "antd", "menu-collapsed");
            expandNavigationButton.PerformClick();
            await Task.Delay(80);
            if (navigationMenu.Collapsed || navigationViewportPanel.Width != width)
                throw new InvalidOperationException("AntdUI menu did not restore its expanded width.");
            foreach (var item in _navigation.Values) AssertNavigationItemFits(item, "expanded menu");
            BringIntoViewAndAssert(_deviceGroupFilter, "AntdUI filter");
            _deviceGroupFilter.ExpandDrop = true;
            await Task.Delay(350);
            var popup = System.Windows.Forms.Application.OpenForms.Cast<Form>()
                .FirstOrDefault(form => form.Visible && form.GetType().Name == "LayeredFormSelectDown");
            if (popup is not AntdUI.ILayeredForm layered || !GetWindowRect(popup.Handle, out var nativeRect))
                throw new InvalidOperationException("AntdUI select popup window is missing.");
            var actualBounds = Rectangle.FromLTRB(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            SaveEditorScreenshot(popup, "antd", "select-popup");
            using var popupFont = new Font(_deviceGroupFilter.Font, FontStyle.Bold);
            var textWidth = ((AntdUI.Select)_deviceGroupFilter).Items.OfType<AntdUI.SelectItem>()
                .Max(item => TextRenderer.MeasureText(item.Text, popupFont, Size.Empty, TextFormatFlags.NoPadding).Width);
            Console.WriteLine($"Select popup bounds: native={actualBounds}; target={layered.TargetRect}; widestText={textWidth}; render={layered.Print()}.");
            // ListAutoWidth follows the demo: short choices may be narrower than the input.
            if (actualBounds.Width < textWidth + UiTheme.Scale(this, 16) || actualBounds.Height < UiTheme.Scale(this, 60)
                || !Screen.FromControl(popup).WorkingArea.Contains(actualBounds) || actualBounds.Size != layered.TargetRect.Size)
                throw new InvalidOperationException("AntdUI select popup is missing or clipped.");
            _deviceGroupFilter.ExpandDrop = false;
            Console.WriteLine("AntdUI controls passed: icon-only/expanded menu, eight routes, actual Select popup within work area.");
        }
        finally
        {
            _deviceGroupFilter.ExpandDrop = false;
            if (navigationMenu.Collapsed) expandNavigationButton.PerformClick();
            ShowPage(originalPage);
        }
    }
}
