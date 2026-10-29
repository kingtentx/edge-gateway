using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeFormWidthsAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("FormWidths requires an isolated stopped host.");
        _refreshTimer.Stop();
        if (ScrollbarsSmoke) { await SmokeScrollbarsAsync(); return; }
        if (SurfacesListsOnly) { await SmokeListSurfacesAsync(); return; }
        var originalSize = Size;
        var directory = SmokeOutputDirectory("form-widths");
        Directory.CreateDirectory(directory);
        foreach (var narrow in new[] { false, true, false })
        {
            Size = narrow ? new Size(MinimumSize.Width, originalSize.Height) : originalSize;
            foreach (var key in _pages.Keys)
            {
                ShowPage(key);
                await Task.Yield();
                PerformLayoutRecursively(this);
                if (SurfacesSmoke) AssertSurfaceStyles(_pages[key]);
                AssertPageToolbarsFit(_pages[key], "form widths " + key);
                if (!Descendants(_pages[key]).OfType<TabControl>().Any()) AssertHorizontalFilters(_pages[key]);
                if (!Descendants(_pages[key]).OfType<TabControl>().Any()) AssertSwitchSizes(_pages[key]);
                foreach (var tabs in Descendants(_pages[key]).OfType<TabControl>().ToArray())
                {
                    foreach (TabPage tab in tabs.TabPages)
                    {
                        tabs.SelectedTab = tab;
                        PerformLayoutRecursively(this);
                        AssertPageToolbarsFit(tab, "form widths " + key + " " + tab.Text);
                        AssertHorizontalFilters(tab);
                        AssertSwitchSizes(tab);
                        ResetScrollPositions(tab, navigationMenu);
                        SaveEditorScreenshot(this, "form-widths", key + "-" + tab.Name + (narrow ? "-narrow" : "-wide"));
                    }
                    tabs.SelectedIndex = 0;
                }
                ResetScrollPositions(_pages[key], navigationMenu);
                SaveEditorScreenshot(this, "form-widths", key + (narrow ? "-narrow" : "-wide"));
            }
        }
        if (SurfacesSmoke) await SmokeListSurfacesAsync();
        // Exercise actual editor controls and dynamic parameters without starting collection or external I/O.
        SmokeDriverEditors();
        SmokeDynamicDriverSwitch();
        var result = new LayoutCaseResult { Name = "form-widths" };
        var area = Screen.FromControl(this).WorkingArea;
        VerifyMatrixDialogs(area, directory, result);
        VerifyDatabaseMatrixDialogs(area, directory, result);
        using (var editor = new DeviceGroupEditor("表单宽度验证"))
        {
            editor.Show(this);
            try { VerifyEditorLayout(editor, "group width"); SaveEditorScreenshot(editor, "form-widths", "group"); }
            finally { editor.Close(); }
        }
        Console.WriteLine($"FormWidths smoke passed: DeviceDpi={DeviceDpi}; eight pages, all page tabs, narrow/wide return, driver editors and dynamic parameters, database/channel/group/write dialogs; compact bounds, focus and clipping.");
    }

    private static void AssertSwitchSizes(Control root)
    {
        foreach (var toggle in Descendants(root).OfType<GatewaySwitch>().Where(toggle => toggle.Visible))
            if (toggle.Width != UiTheme.Scale(toggle, 52) || toggle.Height < UiTheme.Scale(toggle, 30))
                throw new InvalidOperationException($"Switch collapsed below its usable bounds: {toggle.Name} {toggle.Bounds} dpi={toggle.DeviceDpi}");
    }

    private static void AssertHorizontalFilters(Control root)
    {
        foreach (var surface in Descendants(root).Where(control => control is InputFrame or GatewaySwitch))
            AssertFieldCaptionAlignment(surface, "form alignment " + root.Name);
        foreach (var group in Descendants(root).OfType<TableLayoutPanel>().Where(control => control.Visible && control.Name.EndsWith("Filter")))
        {
            if (group.Parent is TabPage { Showed: false }) continue;
            var caption = group.Controls.OfType<Label>().SingleOrDefault();
            var input = group.Controls.OfType<InputFrame>().SingleOrDefault();
            if (caption is null || input is null) continue;
            if (group.GetColumn(input) != 1 || group.GetRow(input) != 0 || caption.Right > input.Left
                || Math.Abs(caption.Top + caption.Height / 2 - input.Top - input.Height / 2) > UiTheme.Scale(group, 3))
                throw new InvalidOperationException($"Filter is not horizontally aligned: {group.Name}, label={caption.Bounds}, input={input.Bounds}.");
        }
    }
}
