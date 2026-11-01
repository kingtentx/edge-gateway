using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool DatabaseSelectionSmoke { get; set; }

    private async Task SmokeDatabaseSelectionAsync()
    {
        _refreshTimer.Stop();
        var connection = RecordingConnectionFixture();
        var first = new DeviceConfiguration { Name = "一号设备", Group = "生产一组", DriverId = "simulator",
            Tags = Enumerable.Range(1, 60).Select(index => new TagConfiguration { Name = "温度 " + index, Address = "sine" }).ToList() };
        var second = new DeviceConfiguration { Name = "二号设备", Group = "生产二组", DriverId = "simulator",
            Tags = [new() { Name = "压力", Address = "constant:1" }] };
        var group = new DatabaseWriteGroupConfiguration { Name = "选点验证", ConnectionId = connection.Id,
            TagIds = [first.Tags[0].Id, second.Tags[0].Id] };
        var original = RecordingSmokeJson(group);
        using var editor = new DatabaseWriteGroupDialog(group, [connection], [first, second], _application);
        editor.Show(this);
        try
        {
            var tabs = Descendants(editor).OfType<TabControl>().First();
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "点位选择");
            await Task.Delay(120);
            var tree = Descendants(editor).OfType<AntdUI.Tree>().Single();
            var filter = Descendants(editor).OfType<ComboBox>().Single(control => control.Name == "DatabasePointGroupFilter");
            var search = Descendants(editor).OfType<GatewayInput>().Single(control => control.Name == "DatabasePointSearch");
            var all = Descendants(editor).OfType<AntdUI.Checkbox>().Single(control => control.Name == "DatabaseAllEnabledPoints");
            var visible = Descendants(editor).OfType<AntdUI.Checkbox>().Single(control => control.Name == "DatabaseVisiblePoints");
            var list = editor.SmokePointList;
            Check(list.Items.Count == 61 && visible.CheckState == CheckState.Indeterminate, "initial selection");
            tree.SelectID(first.Id.ToString(), false);
            Check(list.Items.Count == 60, "device scope");
            VerifyDatabasePointSelectionForSmoke(editor, "device-scoped selector");
            visible.Checked = true;
            Check(editor.CommitForSmoke().TagIds.Count == 61, "select visible lost other device selection");
            visible.Checked = false;
            Check(editor.CommitForSmoke().TagIds.SequenceEqual(new[] { second.Tags[0].Id }), "clear visible changed hidden points");
            filter.SelectedItem = filter.Items.Cast<object>().Single(item => item.ToString() == "生产二组");
            Check(list.Items.Count == 1 && visible.Checked, "group filter or retained checkbox");
            search.Text = "不存在";
            Check(list.Items.Count == 0 && !visible.Enabled && editor.CommitForSmoke().TagIds.Count == 1, "empty search lost selection");
            search.Text = "constant";
            Check(list.Items.Count == 1, "address search");
            all.Checked = true;
            Check(!list.Enabled && editor.CommitForSmoke().TagIds.Count == 0, "all-enabled semantics");
            all.Checked = false;
            Check(list.Enabled && editor.CommitForSmoke().TagIds.SequenceEqual(new[] { second.Tags[0].Id }), "manual draft restoration");
            search.Text = ""; filter.SelectedIndex = 0;
            tree.Select(tree.Items[0].Sub[0], false);
            Check(list.Items.Count == 60, "group tree node scope");
            list.SetItemChecked(0, true);
            list.TopIndex = 45;
            Check(list.YScroll?.Value == -list.AutoScrollPosition.Y, "checkbox list scrollbar sync");
            var check = list.Controls.OfType<AntdUI.Checkbox>().First(control => control.Visible);
            var index = (int)check.Tag!;
            var before = list.Items.Checked[index];
            check.Focus();
            SendControlSmokeMessage(check.Handle, 0x0100, new IntPtr((int)Keys.Space), IntPtr.Zero);
            SendControlSmokeMessage(check.Handle, 0x0101, new IntPtr((int)Keys.Space), IntPtr.Zero);
            Check(list.Items.Checked[index] != before, "official checkbox keyboard");
            var point = new IntPtr(((check.Height / 2) << 16) | (check.Width / 2));
            SendControlSmokeMessage(check.Handle, 0x0201, new IntPtr(1), point);
            SendControlSmokeMessage(check.Handle, 0x0202, IntPtr.Zero, point);
            Check(list.Items.Checked[index] == before, "official checkbox mouse");
            foreach (var size in new[] { new Size(1000, 650), new Size(640, 480), new Size(1000, 650) })
            {
                editor.ClientSize = new Size(UiTheme.Scale(editor, size.Width), UiTheme.Scale(editor, size.Height));
                await Task.Delay(100);
                PerformLayoutRecursively(editor);
                BringIntoViewAndAssert(tree, "device tree");
                BringIntoViewAndAssert(search, "point search");
                BringIntoViewAndAssert(list, "point list");
                Check(tree.RectangleToScreen(tree.ClientRectangle).Right <= list.RectangleToScreen(list.ClientRectangle).Left, "left/right layout overlap");
                SaveEditorScreenshot(editor, "database-selection", size.Width == 640 ? "narrow" : "wide");
            }
            Check(RecordingSmokeJson(group) == original, "cancel changed source configuration");
            editor.InspectSavedConfiguration();
            var selectedBeforeInspection = list.Items.Checked.ToArray();
            list.SetItemChecked(0, !list.Items.Checked[0]);
            Check(list.ReadOnly && list.Enabled && selectedBeforeInspection.SequenceEqual(list.Items.Checked),
                "read-only point inspection changed selection or disabled scrolling");
            search.Text = "温度 1";
            Check(list.Items.Count > 0 && list.ReadOnly && !all.Enabled && !visible.Enabled,
                "read-only search lost mutation guards");
            var applyCalled = false;
            editor.SetApplyHandler(() => { applyCalled = true; return Task.CompletedTask; }, false, _ => { });
            var rejected = false;
            try { await editor.ApplyAndAcceptForSmokeAsync(); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !applyCalled && editor.SmokeMapping.ReadOnly, "inspection allowed save or mapping edit");
            SaveEditorScreenshot(editor, "database-selection", "inspection");
            using var connectionView = new DatabaseConnectionDialog(connection, _application);
            connectionView.InspectSavedConfiguration();
            Check(connectionView.SmokeInputs.All(input => !input.Enabled), "connection inspection allows editing");
            Check(RecordingSmokeJson(group) == original, "inspection changed saved source");
        }
        finally { editor.Close(); }
        using (var dynamicEditor = new DatabaseWriteGroupDialog(group, [connection], [first, second], _application, ["空分组"]))
        {
            dynamicEditor.Show(this);
            try
            {
                var tabs = Descendants(dynamicEditor).OfType<TabControl>().First();
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "点位选择");
                var mode = CapabilityControl<ComboBox>(dynamicEditor, "DatabaseSelectionMode");
                var target = CapabilityControl<ComboBox>(dynamicEditor, "DatabaseSelectionTarget");
                mode.SelectedIndex = 1; target.SelectedIndex = 0;
                var draft = dynamicEditor.CommitForSmoke();
                Check(draft.Selection.Mode == DatabasePointSelectionMode.Device && draft.Selection.DeviceId == first.Id && draft.TagIds.Count == 0, "dynamic device rule was not saved");
                Check(DatabasePointSelection.Resolve(draft, [first, second]).SetEquals(first.Tags.Select(tag => tag.Id)), "dynamic rule includes other device");
                mode.SelectedIndex = 2; target.SelectedIndex = 1;
                draft = dynamicEditor.CommitForSmoke();
                Check(draft.Selection.Mode == DatabasePointSelectionMode.Group && draft.Selection.DeviceId is null
                    && DatabasePointSelection.Resolve(draft, [first, second]).SetEquals(second.Tags.Select(tag => tag.Id)), "dynamic group rule was not saved");
                target.SelectedIndex = 2;
                Check(DatabasePointSelection.Resolve(dynamicEditor.CommitForSmoke(), [first, second]).Count == 0, "empty dynamic group expanded to all points");
                target.SelectedIndex = 1;
                dynamicEditor.ClientSize = new Size(UiTheme.Scale(dynamicEditor, 640), UiTheme.Scale(dynamicEditor, 480));
                await Task.Delay(100); PerformLayoutRecursively(dynamicEditor);
                BringIntoViewAndAssert(mode, "dynamic selection mode"); BringIntoViewAndAssert(target, "dynamic selection target");
                Check(Math.Abs(mode.PointToScreen(Point.Empty).Y - target.PointToScreen(Point.Empty).Y) <= 2, "selection filters are stacked instead of side by side");
                Check(dynamicEditor.SmokePointList.Items.Count == 1, "dynamic preview shows unrelated devices");
                Check(dynamicEditor.SmokePointList.Height >= UiTheme.Scale(dynamicEditor, 72), "dynamic rules squeezed point rows out of narrow window");
                SaveEditorScreenshot(dynamicEditor, "database-selection", "dynamic-rule");
                dynamicEditor.InspectSavedConfiguration();
                Check(!mode.Enabled && !target.Enabled, "dynamic inspection permits mutation");
            }
            finally { dynamicEditor.Close(); }
        }
        Console.WriteLine($"Database selection smoke passed: groups/devices, checkbox/keyboard, visible selection, search, draft isolation, scrolling and width round trip; DeviceDpi={DeviceDpi}.");
        static void Check(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException("Database point selection: " + detail); }
    }
}
