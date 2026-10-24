using System.Runtime.InteropServices;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsNavigationWindow(IntPtr window);

    private void SmokeDeferredPageNavigation()
    {
        var originalPage = _activePage;
        var pages = new[] { ("database", _databaseTabs), ("cloud", _cloudTabs), ("logs", _logTabs) };
        try
        {
            // A deliberately stale metric makes this lifecycle regression reproducible
            // at 96 DPI as well. This does not change or simulate the window's DPI.
            foreach (var (_, tabs) in pages)
            {
                if (tabs.IsHandleCreated)
                    throw new InvalidOperationException(tabs.Name + ": navigation regression must run before first display.");
                tabs.Padding = Point.Empty;
            }

            for (var pass = 0; pass < 3; pass++)
            foreach (var (key, tabs) in pages)
            {
                _navigation[key].PerformClick();
                PerformLayoutRecursively(this);
                if (_activePage != key || !_pages[key].Visible || !tabs.Visible)
                    throw new InvalidOperationException(key + ": navigation did not display the selected page.");
                var expected = new Point(UiTheme.Scale(tabs, 18), UiTheme.Scale(tabs, 9));
                if (!tabs.IsHandleCreated || !IsNavigationWindow(tabs.Handle) || tabs.Padding != expected)
                    throw new InvalidOperationException(key + ": deferred tab creation lost its native window or current DPI padding.");

                var handle = tabs.Handle;
                foreach (TabPage tab in tabs.TabPages)
                {
                    tabs.SelectedTab = tab;
                    PerformLayoutRecursively(tabs);
                    if (!tab.Visible || !tab.IsHandleCreated || !IsNavigationWindow(tab.Handle) || tabs.Handle != handle)
                        throw new InvalidOperationException(key + ": tab selection lost its native window or recreated the tab control.");
                }
                tabs.SelectedIndex = 0;
                _navigation["overview"].PerformClick();
            }
            Console.WriteLine($"Deferred tab navigation passed: database/platform/logs first display with stale padding, three menu cycles and all tabs at actual {DeviceDpi} DPI.");
        }
        finally { _navigation[originalPage].PerformClick(); }
    }
}
