using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool DialogTitlesSmoke { get; set; }
    private async Task SmokeDialogTitlesAsync()
    {
        _refreshTimer.Stop();
        using (var empty = new EditorDialog())
        {
            empty.Show(this); await Task.Delay(30);
            empty.SetApplyHandler(() => Task.CompletedTask, true, _ => { });
            empty.PerformLayout(); empty.VerifyHeadingForSmoke("empty description becomes running warning");
            if (!Descendants(empty).Single(control => control.Name == "EditorHeadingDescription").Visible)
                throw new InvalidOperationException("Running device warning did not become visible.");
            empty.Close();
        }
        var device = CreateConfigurationSmokeDevice(_drivers.GetDescriptor("modbus-rtu"));
        var connection = RecordingConnectionFixture();
        using var configuration = new DeviceConfigurationDialog(device, _drivers);
        using var parameter = new DeviceParameterDialog();
        using var tag = new TagEditor(TagEditor.CreateDefaultTag(_drivers.GetDescriptor("simulator"), "温度"), _drivers.GetDescriptor("simulator"));
        using var database = new DatabaseConnectionDialog(connection, _application);
        using var group = new DatabaseWriteGroupDialog(RecordingMappingFixture(connection.Id, device.Tags[0].Id), [connection], [device], _application);
        using var preview = new DatabasePreviewDialog();
        using var import = new DeviceWorkbookImportDialog(_ => new([], 0, []));
        using var channel = new ChannelEditor(new ChannelConfiguration { Name = "通道" });
        using var name = new DeviceGroupEditor("设备分组");
        using var points = new DevicePointsDialog { Text = "演示机组 · 点位管理" };
        using var catalog = new ConfigurationCatalogDialog { Text = "设备分组管理" };
        using var write = new WriteValueDialog("演示机组", "目标温度", "℃", "45", TagDataType.Float64);
        foreach (var dialog in new Form[] { configuration, parameter, tag, database, group, preview, import, channel, name, points, catalog, write })
        {
            dialog.Show(this);
            try
            {
                var wide = dialog.Size;
                foreach (var narrow in new[] { false, true, false })
                {
                    dialog.Size = narrow ? new Size(Math.Min(wide.Width, Math.Max(dialog.MinimumSize.Width, UiTheme.Scale(dialog, 600))), wide.Height) : wide;
                    await Task.Delay(120);
                    if (string.IsNullOrWhiteSpace(dialog.Text)) throw new InvalidOperationException("Window title missing.");
                    if (dialog is EditorDialog editor)
                    {
                        foreach (var tab in editor.SmokeTabs)
                        {
                            editor.SelectedSmokeTab = tab;
                            VerifyEditorLayout(editor, "dialog titles " + dialog.GetType().Name);
                        }
                        var tabs = Descendants(editor).OfType<GatewayTabs>().Single();
                        if (tabs.TabMenuVisible != (tabs.TabPages.Count > 1)) throw new InvalidOperationException("Single-page tab chrome was retained.");
                    }
                    foreach (var label in Descendants(dialog).OfType<Label>().Where(label => label.Visible && label.Font.Size >= 16))
                        throw new InvalidOperationException("Duplicate large heading remains: " + label.Text);
                    foreach (var button in Descendants(dialog).OfType<Button>().Where(button => button.Visible))
                        BringIntoViewAndAssert(button, "dialog command " + button.Name);
                    if (dialog.CancelButton is Control cancel) BringIntoViewAndAssert(cancel, "dialog cancel");
                    SaveEditorScreenshot(dialog, "dialog-titles", dialog.GetType().Name + (narrow ? "-narrow" : "-wide"));
                }
            }
            finally { dialog.Close(); }
        }
        Console.WriteLine("Dialog titles smoke passed: 12 dialogs, window-only titles, single-page tab chrome, descriptions/actions, narrow-wide return; DeviceDpi=" + DeviceDpi);
    }
}
