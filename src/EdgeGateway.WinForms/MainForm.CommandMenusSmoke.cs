namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeCommandMenusAsync()
    {
        ShowPage("devices");
        foreach (var button in new[] { _deviceExchange, _devicesPageView.deviceBatchButton })
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
        var other = _configuration.Devices.First(device => device.Id != target.Id);
        RebindConfigurationViews(other.Id);
        await SmokeDeviceConfigurationModalAsync(() => ConfigureDeviceAsync(target.Id), editor =>
        {
            var name = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "DeviceConfiguration_Name");
            if (name.Text != target.Name) throw new InvalidOperationException("Configuration changed its target.");
            VerifyEditorLayout(editor, "row device form");
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
        }, "device row identity");
        RebindConfigurationViews(target.Id);
        _deviceSearch.Text = "no-device-menu-check";
        OpenMenuForSmoke(_devicesPageView.deviceBatchButton, "disabled menu");
        var disabled = MenuItem(_devicesPageView.deviceBatchButton, "BatchCopyDevices");
        if (disabled.Enabled || !string.IsNullOrEmpty(disabled.SubText))
            throw new InvalidOperationException("Disabled batch menu must not show hint text.");
        await _devicesPageView.deviceBatchButton.ExecuteCommandAsync((MenuCommand)disabled.Tag!);
        SaveEditorScreenshot(_devicesPageView.deviceBatchButton.OpenedMenu, "menus", "disabled");
        _devicesPageView.deviceBatchButton.OpenedMenu.Close();
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
        Console.WriteLine("CommandMenus passed: three official popups, arrows, keyboard opening/Escape, row target, disabled commands and accessibility, aligned scanning fields.");
    }
}
