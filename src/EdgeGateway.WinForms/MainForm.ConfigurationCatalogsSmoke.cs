namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeConfigurationCatalogsAsync()
    {
        var original = Clone(_configuration);
        var page = _activePage;
        var selected = SelectedDevice()?.Id;
        ShowPage("devices");
        try
        {
            foreach (var groups in new[] { true, false })
            {
                Exception? failure = null;
                var observed = false;
                using var timer = new System.Windows.Forms.Timer { Interval = 40 };
                timer.Tick += async (_, _) =>
                {
                    var manager = System.Windows.Forms.Application.OpenForms.OfType<ConfigurationCatalogDialog>()
                        .SingleOrDefault(window => window.Visible && window.Modal);
                    if (manager is null) return;
                    timer.Stop(); observed = true;
                    try
                    {
                        CheckUsability(manager.Owner == this && manager.addButton.Enabled, "catalog ownership or add command incorrect.");
                        var name = groups ? "目录验证分组" : "目录验证通道";
                        await EditCatalogForSmokeAsync(manager, manager.addButton, groups, name, rejectFirst: false);
                        CheckUsability(manager.Selected?.Name == name && manager.Selected.DeviceCount == 0
                            && manager.deleteButton.Enabled, "new empty catalog item was not selected/deletable.");
                        var key = manager.Selected!.Key;
                        await EditCatalogForSmokeAsync(manager, manager.editButton, groups, name + "已编辑", rejectFirst: true);
                        CheckUsability(manager.Selected?.Name == name + "已编辑", "catalog edit not reflected in list.");
                        if (!groups) CheckUsability(manager.Selected!.Key == key, "channel edit changed stable identity.");
                        if (groups)
                        {
                            CheckUsability(_deviceGroupFilter.Items.Cast<GroupChoice>().Any(item => item.Group == name + "已编辑"),
                                "empty group is missing from device filters.");
                            using var deviceEditor = new DeviceConfigurationDialog(_configuration.Devices[0], _drivers,
                                channels: _configuration.Channels, groups: DeviceGroupNames());
                            var groupInput = Descendants(deviceEditor).OfType<ComboBox>().Single(input => input.Name == "DeviceConfiguration_Group");
                            CheckUsability(groupInput.Items.Contains(name + "已编辑"), "saved groups missing from device editor.");
                            var referenced = manager.catalogGrid.Rows.Cast<DataGridViewRow>()
                                .First(row => ((ConfigurationCatalogRow)row.DataBoundItem).DeviceCount > 0);
                            manager.catalogGrid.CurrentCell = referenced.Cells[0];
                            CheckUsability(!manager.deleteButton.Enabled && manager.editButton.Enabled, "referenced group deletion is available.");
                        }
                        SaveEditorScreenshot(manager, "catalog", groups ? "groups" : "channels");
                        // Focus checks use the newly created, unreferenced row; the referenced
                        // row's delete command was deliberately disabled and checked above.
                        manager.catalogGrid.CurrentCell = manager.catalogGrid.Rows.Cast<DataGridViewRow>()
                            .Single(row => ((ConfigurationCatalogRow)row.DataBoundItem).Name == name + "已编辑").Cells[0];
                        manager.ClientSize = new Size(UiTheme.Scale(manager, 620), UiTheme.Scale(manager, 400));
                        PerformLayoutRecursively(manager);
                        foreach (var button in new[] { manager.addButton, manager.editButton, manager.deleteButton, manager.closeButton })
                            BringIntoViewAndAssert(button, "compact catalog action");
                        CheckUsability(manager.catalogGrid.Height >= manager.catalogGrid.ColumnHeadersHeight + manager.catalogGrid.RowTemplate.Height * 4,
                            "catalog list collapsed in compact window.");
                        SaveEditorScreenshot(manager, "catalog", groups ? "groups-compact" : "channels-compact");
                        await AssertSavedConfigurationForSmokeAsync("catalog CRUD");
                    }
                    catch (Exception exception) { failure = exception; TrySaveSmokeFailure(manager, "catalog-failed"); }
                    finally { manager.DialogResult = DialogResult.Cancel; }
                };
                timer.Start();
                try { (groups ? _devicesPageView.groupButton : _devicesPageView.channelButton).PerformClick(); }
                finally { timer.Stop(); }
                if (failure is not null) throw new InvalidOperationException("Catalog UI smoke failed.", failure);
                CheckUsability(observed, "catalog toolbar did not open the manager.");
            }
        }
        finally
        {
            await RestoreConfigurationForSmokeAsync(original);
            RebindConfigurationViews(selected); ShowPage(page);
        }
        Console.WriteLine($"Catalog UI smoke passed at actual {DeviceDpi} DPI: toolbar entry, empty group creation, channel create/edit, stable channel ID, referenced delete guard, editor validation retention, device group choices and compact layout.");
    }

    private async Task EditCatalogForSmokeAsync(ConfigurationCatalogDialog manager, AntdUI.Button action, bool groups, string name, bool rejectFirst)
    {
        Exception? failure = null;
        var observed = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += async (_, _) =>
        {
            var editor = System.Windows.Forms.Application.OpenForms.OfType<EditorDialog>()
                .SingleOrDefault(window => window.Visible && window.Modal && window.Owner == manager);
            if (editor is null) return;
            timer.Stop(); observed = true;
            try
            {
                var input = Descendants(editor).OfType<TextBox>().Single(control => control.Name == (groups ? "DeviceGroupName" : "ChannelName"));
                editor.SuppressApplyErrorDialogForSmoke = true;
                if (rejectFirst)
                {
                    input.Text = "";
                    try { await editor.ApplyAndAcceptForSmokeAsync(); }
                    catch (Exception) when (editor.LastApplyErrorForSmoke is not null) { }
                    CheckUsability(editor.Visible && editor.LastApplyErrorForSmoke is not null && input.Text.Length == 0,
                        "catalog editor validation lost draft.");
                }
                input.Text = name;
                if (rejectFirst)
                {
                    var before = DeviceImportSmokeJson(_configuration);
                    using (RejectConfigurationWritesForSmoke())
                    {
                        var rejected = false;
                        try { await editor.ApplyAndAcceptForSmokeAsync(); }
                        catch (Exception exception) when (IsConfigurationWriteFailureForSmoke(exception)) { rejected = true; }
                        CheckUsability(rejected && editor.Visible && input.Text == name
                            && DeviceImportSmokeJson(_configuration) == before, "catalog failed save lost draft or changed applied configuration.");
                    }
                    CheckUsability(DeviceImportSmokeJson(await _application.ReadSavedConfigurationAsync()) == before,
                        "catalog failed save changed persisted configuration.");
                }
                VerifyEditorLayout(editor, "catalog editor");
                SaveEditorScreenshot(editor, "catalog", groups ? "group-editor" : "channel-editor");
                await editor.ApplyAndAcceptForSmokeAsync();
            }
            catch (Exception exception) { failure = exception; editor.DialogResult = DialogResult.Cancel; }
            finally { editor.Close(); }
        };
        timer.Start();
        try { action.PerformClick(); }
        finally { timer.Stop(); }
        if (failure is not null) throw new InvalidOperationException("Catalog nested editor failed.", failure);
        CheckUsability(observed, "catalog action did not open its editor.");
        await Task.CompletedTask;
    }
}
