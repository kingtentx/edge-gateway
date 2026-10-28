namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeCommandMenusAsync()
    {
        ShowPage("devices");
        foreach (var button in new[] { _deviceExchange, _deviceMore })
        {
            OpenMenuForSmoke(button, "command appearance " + button.Name, keyboard: true);
            await Task.Delay(300);
            SendMenuKeyForSmoke(button, Keys.Down);
            SaveEditorScreenshot(button.OpenedMenu, "menus", button.Name);
            if (button.AccessibilityObject.Role != AccessibleRole.ButtonDropDown
                || button.AccessibilityObject.GetChildCount() != button.OpenedCommands.Count)
                throw new InvalidOperationException("Dropdown accessibility commands are missing.");
            SendMenuKeyForSmoke(button, Keys.Escape);
            await Task.Delay(300);
            if (button.OpenedMenu.Visible || button.ArrowProg != -1)
                throw new InvalidOperationException("Escape did not close menu/reset arrow.");
        }
        var target = SelectedDevice() ?? throw new InvalidOperationException("No menu target.");
        OpenMenuForSmoke(_deviceMore, "captured device menu", keyboard: true);
        var other = _configuration.Devices.First(device => device.Id != target.Id);
        RebindConfigurationViews(other.Id);
        await SmokeDeviceConfigurationModalAsync(async () =>
        {
            SendMenuKeyForSmoke(_deviceMore, Keys.Down);
            SendMenuKeyForSmoke(_deviceMore, Keys.Enter);
            await Task.Delay(800);
        }, editor =>
        {
            var name = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "DeviceConfiguration_Name");
            if (name.Text != target.Name) throw new InvalidOperationException("Menu changed its captured target.");
            VerifyEditorLayout(editor, "menu device form");
            foreach (var tabs in Descendants(editor).OfType<TabControl>())
            {
                var scanning = tabs.TabPages.Cast<TabPage>().FirstOrDefault(tab => tab.Text == "扫描与恢复");
                if (scanning is null) continue;
                tabs.SelectedTab = scanning;
                PerformLayoutRecursively(editor);
                var fields = Descendants(scanning).OfType<InputFrame>().Where(frame => frame.Visible).ToArray();
                if (fields.Select(frame => frame.Width).Distinct().Count() != 1)
                    throw new InvalidOperationException("Scanning form widths differ.");
                SaveEditorScreenshot(editor, "menus", "scanning-form");
            }
        }, "official keyboard menu captured identity");
        var completed = System.Diagnostics.Stopwatch.StartNew();
        while (_deviceMore.IsExecuting && completed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        if (_deviceMore.IsExecuting) throw new InvalidOperationException("Device menu did not finish its modal command.");
        RebindConfigurationViews(target.Id);
        _deviceSearch.Text = "no-device-menu-check";
        OpenMenuForSmoke(_deviceMore, "disabled menu");
        var disabled = MenuItem(_deviceMore, "ConfigureDevice");
        if (disabled.Enabled || string.IsNullOrWhiteSpace(disabled.SubText))
            throw new InvalidOperationException("Disabled menu reason missing.");
        await _deviceMore.ExecuteCommandAsync((MenuCommand)disabled.Tag!);
        SaveEditorScreenshot(_deviceMore.OpenedMenu, "menus", "disabled");
        _deviceMore.OpenedMenu.Close();
        _deviceSearch.Clear();
        RebindConfigurationViews(target.Id);
        _pointsDialog.Show(this);
        try
        {
            OpenMenuForSmoke(_tagMore, "point dropdown", keyboard: true);
            await Task.Delay(300);
            SaveEditorScreenshot(_tagMore.OpenedMenu, "menus", "points");
            SendMenuKeyForSmoke(_tagMore, Keys.Escape);
        }
        finally { _pointsDialog.Hide(); }
        BringIntoViewAndAssert(_deviceGroupFilter, "aligned Select popup");
        _deviceGroupFilter.ExpandDrop = true;
        await Task.Delay(350);
        try
        {
            var popup = System.Windows.Forms.Application.OpenForms.Cast<Form>()
                .Single(form => form.Visible && form.GetType().Name == "LayeredFormSelectDown");
            if (_deviceGroupFilter.ListAutoWidth || popup is not AntdUI.ILayeredForm layered
                || Math.Abs(layered.TargetRect.Width - _deviceGroupFilter.Width) > UiTheme.Scale(this, 32))
                throw new InvalidOperationException("Select popup does not follow input width.");
            SaveEditorScreenshot(popup, "menus", "select-width");
        }
        finally { _deviceGroupFilter.ExpandDrop = false; }
        Console.WriteLine("CommandMenus passed: three official popups, arrows, keyboard selection/Enter/Escape, captured target, disabled reasons and accessibility, aligned scanning fields.");
    }
}
