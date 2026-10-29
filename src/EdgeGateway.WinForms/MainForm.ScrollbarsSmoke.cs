namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool ScrollbarsSmoke { get; set; }

    private async Task SmokeScrollbarsAsync()
    {
        ShowPage("overview");
        await SmokeInitialOverviewScrollAsync();
        var checkedBars = 0;
        foreach (var key in _pages.Keys)
        {
            ShowPage(key);
            await Task.Yield();
            PerformLayoutRecursively(this);
            CheckBars(_pages[key]);
            foreach (var tabs in Descendants(_pages[key]).OfType<TabControl>().ToArray())
                foreach (TabPage tab in tabs.TabPages)
                {
                    tabs.SelectedTab = tab;
                    PerformLayoutRecursively(this);
                    await Task.Delay(60);
                    CheckBars(tab);
                }
        }
        using var dialog = new Form { Size = new Size(500, 360) };
        using var list = new GatewayCheckList { Dock = DockStyle.Fill };
        dialog.Controls.Add(list);
        for (var i = 0; i < 100; i++) list.Items.Add("滚动验证点位 " + i, false);
        dialog.Show(this);
        try
        {
            await Task.Yield();
            CheckBars(dialog);
            list.TopIndex = 70;
            if (list.TopIndex < 60 || !Descendants(list).OfType<Label>().Any(label => label.Visible && label.Text.Contains("70")))
                throw new InvalidOperationException("Virtual point rows did not follow the official scrollbar.");
            list.SetItemChecked(70, true);
            if (!list.Items.Checked[70]) throw new InvalidOperationException("Point check state was not retained after scrolling.");
        }
        finally { dialog.Close(); }
        if (checkedBars == 0) throw new InvalidOperationException("No official scrollbars exercised.");
        Console.WriteLine($"Official scrollbar smoke passed: {checkedBars} panel scrollbars; eight pages/tabs, wheel, thumb messages, refresh position and virtual point rows.");

        void CheckBars(Control root)
        {
            bool Active(Control control)
            {
                for (Control? parent = control; parent is not null; parent = parent.Parent)
                    if (!parent.Visible || parent is TabPage { Showed: false }) return false;
                return true;
            }
            foreach (var native in Descendants(root).OfType<ScrollableControl>().Where(control => Active(control) && control.AutoScroll && control.VerticalScroll.Visible))
                if (native.Parent is not AntdUI.Panel)
                    throw new InvalidOperationException("Native vertical scrollbar remains: " + native.Name);
            foreach (var panel in Descendants(root).OfType<AntdUI.Panel>().Where(panel => Active(panel) && panel.AutoScroll).ToArray())
            {
                panel.Refresh();
                if (panel.Controls.Owner is not ScrollableControl host || !host.VerticalScroll.Visible) continue;
                if (panel.YScroll is not { Visible: true } bar)
                    throw new InvalidOperationException("Scrollable content has no official scrollbar: " + panel.Name);
                var position = new Point(-host.AutoScrollPosition.X, -host.AutoScrollPosition.Y);
                bar.Value = 0;
                var start = new IntPtr((2 << 16) | (bar.Width / 2));
                var end = new IntPtr(((bar.Height - 2) << 16) | (bar.Width / 2));
                SendControlSmokeMessage(bar.Handle, 0x0201, new IntPtr(1), start);
                SendControlSmokeMessage(bar.Handle, 0x0200, new IntPtr(1), end);
                SendControlSmokeMessage(bar.Handle, 0x0202, IntPtr.Zero, end);
                if (host.AutoScrollPosition.Y >= 0) throw new InvalidOperationException($"Official thumb drag did not scroll {panel.Name}: bar={bar.Bounds}/{bar.Maximum}/{bar.Value}, host={host.ClientSize}/{host.VerticalScroll.Maximum}/{host.VerticalScroll.Value}.");
                host.AutoScrollPosition = position;
                checkedBars++;
            }
        }
    }
}
