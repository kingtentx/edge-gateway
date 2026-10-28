using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeUsabilityAsync()
    {
        if (!_smokeTest || _application.IsRunning || _dirty)
            throw new InvalidOperationException("Usability smoke requires an isolated, stopped application.");
        var original = Clone(_configuration);
        var page = _activePage;
        var originalSelection = SelectedDevice()?.Id;
        try
        {
            var a = new DeviceConfiguration
            {
                Name = "可用性验证 A", Group = "布局验证", DriverId = "simulator",
                Tags = Enumerable.Range(1, 30).Select(index => new TagConfiguration
                {
                    Name = $"工艺点位 {index:000}", Address = "constant:" + index,
                    DataType = TagDataType.Float64, Unit = "℃"
                }).ToList()
            };
            var b = new DeviceConfiguration
            {
                Name = "可用性验证 B", DriverId = "simulator",
                Tags = [new TagConfiguration { Name = "B 点位", Address = "constant:1", DataType = TagDataType.Float64 }]
            };
            var fixture = Clone(original); fixture.AutoStart = false;
            fixture.Mqtt = new MqttConfiguration { Enabled = false };
            fixture.Devices = [a, b]; fixture.Channels = [];
            await CommitConfigurationAsync(fixture, a.Id, replaceCloudDraft: true);
            ShowPage("devices"); PerformLayoutRecursively(this);
            await SmokePointDialogRowAsync(a, b);
            RebindConfigurationViews(a.Id);
            CheckUsability(_tagHeading.Text.Contains(a.Name, StringComparison.Ordinal), "point context omitted its device.");
            var selected = _tagGrid.Rows.Cast<DataGridViewRow>().Single(row => row.DataBoundItem is TagRow tag && tag.Id == a.Tags[22].Id);
            _tagGrid.CurrentCell = selected.Cells["Address"];
            var tagId = SelectedTag()!.Id;
            BindTags();
            CheckUsability(SelectedTag()?.Id == tagId && _tagGrid.CurrentCell?.OwningColumn.Name == "Address",
                "rebinding point data lost the selected identity or column.");
            _tagSearch.Text = "023";
            CheckUsability(_tagGrid.Rows.Count == 1 && SelectedTag()?.Id == a.Tags[22].Id && _tagSummary.Text.Contains("1 / 共 30"),
                "point filtering or matching count is incorrect.");
            _tagSearch.Clear();

            _deviceSearch.Text = "no-such-device-for-usability-smoke";
            OpenMenuForSmoke(_deviceMore, "empty device menu");
            CheckUsability(!MenuItem(_deviceMore, "ConfigureDevice").Enabled && !MenuItem(_deviceMore, "DeleteDevice").Enabled,
                "selection commands remain enabled without a selected device.");
            _deviceMore.OpenedMenu.Close();
            _deviceSearch.Clear(); RebindConfigurationViews(a.Id);
            OpenMenuForSmoke(_deviceMore, "device command menu", keyboard: true);
            var configure = MenuItem(_deviceMore, "ConfigureDevice");
            SendMenuKeyForSmoke(_deviceMore, Keys.Down);
            CheckUsability(configure.Enabled, "menu command is unavailable.");
            SaveUsabilityMenuScreenshot(_deviceMore.OpenedMenu);
            // The open menu owns A's identity even if a background rebind changes
            // selection to B before activation.
            RebindConfigurationViews(b.Id);
            await SmokeDeviceConfigurationModalAsync(() => _deviceMore.ExecuteCommandAsync((MenuCommand)configure.Tag!), editor =>
            {
                var name = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "DeviceConfiguration_Name");
                CheckUsability(name.Text == a.Name, "an opened menu redirected its target to a later selection.");
                editor.SetFieldForSmoke("Group", "从菜单保存");
            }, "captured menu device identity");
            _deviceMore.OpenedMenu.Close();
            CheckUsability(_configuration.Devices.Single(device => device.Id == a.Id).Group == "从菜单保存"
                && _configuration.Devices.Single(device => device.Id == b.Id).Group == b.Group, "menu edited another device.");

            _pointsDialog.Show(this);
            OpenMenuForSmoke(_tagMore, "point menu");
            CheckUsability(MenuItem(_tagMore, "ImportTags").Enabled && MenuItem(_tagMore, "TagTemplate").Enabled,
                "point import/template commands are missing.");
            _tagMore.OpenedMenu.Close();
            _pointsDialog.Hide();

            ShowPage("cloud");
            CheckUsability(_cloudTabs.TabPages.Count == 4, "platform settings were not divided into four tabs.");
            foreach (var field in _cloudFields.Values) BringIntoViewAndAssert(field, "platform tab field " + field.Name);
            _cloudTabs.SelectedIndex = 0;
            ((TextBox)_cloudFields["host"]).Text = "usability-broker.invalid";
            CheckUsability(_dirty && _cloudApply.Enabled && _cloudReset.Enabled, "platform draft actions did not activate.");
            var platform = (ComboBox)_cloudFields["platform"];
            platform.SelectedItem = MqttPlatform.ThingsCloud;
            CheckUsability(_cloudFields["typeKey"].Enabled && !_cloudFields["topic"].Enabled
                && !_cloudFields["platformGatewayId"].Enabled, "ThingsCloud field availability is incorrect.");
            ((TextBox)_cloudFields["typeKey"]).Text = "retained-platform-draft";
            platform.SelectedItem = MqttPlatform.Generic;
            CheckUsability(!_cloudFields["typeKey"].Enabled && _cloudFields["topic"].Enabled
                && ((TextBox)_cloudFields["typeKey"]).Text == "retained-platform-draft",
                "switching platforms lost a draft or left another platform's field editable.");
            BringIntoViewAndAssert(_cloudReset, "reset platform draft"); _cloudReset.PerformClick();
            CheckUsability(!_dirty && ((TextBox)_cloudFields["host"]).Text == fixture.Mqtt.Host && !_cloudApply.Enabled
                && ((TextBox)_cloudFields["typeKey"]).Text == fixture.Mqtt.ThingsCloudTypeKey,
                "reset did not restore the saved platform configuration.");
            ResetScrollPositions(_pages["cloud"], navigationMenu);
            SaveEditorScreenshot(this, "usability", "platform");

            ShowPage("realtime"); SelectChoice(_realtimeDevice, a.Id);
            var timestamp = DateTimeOffset.UtcNow;
            var samples = a.Tags.Select((tag, index) => new TelemetrySample(a.Id, a.Name, tag.Id, tag.Name,
                tag.Address, (double)index, (double)index, SampleQuality.Good, timestamp, tag.Unit)).ToList();
            RefreshRealtime(samples);
            GridPagination.GetPager(_realtimeGrid).PageSize = 50;
            _realtimeGrid.CurrentCell = _realtimeGrid.Rows[20].Cells["Error"];
            var before = GridViewState.Capture(_realtimeGrid, row => ((SampleRow)row).Sample.TagId);
            RefreshRealtime(samples.Select(sample => sample with { Value = 42D, Timestamp = timestamp.AddSeconds(1) }).ToList());
            var after = GridViewState.Capture(_realtimeGrid, row => ((SampleRow)row).Sample.TagId);
            CheckUsability(before.SelectedKey!.Equals(after.SelectedKey) && before.ColumnName == after.ColumnName
                && before.FirstVisibleIndex == after.FirstVisibleIndex && before.HorizontalOffset == after.HorizontalOffset,
                "live refresh changed the operator's row/column/scroll position.");

            ShowPage("history"); SelectChoice(_historyDevice, a.Id); SelectChoice(_historyTag, a.Tags[0].Id);
            CheckUsability(HistoryFromLogs && _historyQuery.Enabled, "local log history must be available without external storage.");
            _historySource.SelectedIndex = 1;
            CheckUsability(!_historyQuery.Enabled && _historySummary.Text.Contains("尚未配置"), "history without external storage did not show its empty state.");
            // This fixture checks presentation only; actual database query behavior has separate provider integration tests.
            var presentationQuery = new HistoryQuery(a.Id, a.Tags[0].Id, new DateTimeOffset(_historyFrom.Value), new DateTimeOffset(_historyTo.Value), (int)_historyLimit.Value);
            PresentHistoryResults(null, "界面测试数据", a.Name, a.Tags[0].Name, presentationQuery, samples.Where(sample => sample.TagId == a.Tags[0].Id).ToArray());
            var executed = _executedHistoryQuery;
            _historyFrom.Value = _historyFrom.Value.AddMinutes(-5);
            CheckUsability(_historyConditionsChanged && _executedHistoryQuery == executed
                && _historySummary.Text.Contains("上次结果", StringComparison.Ordinal), "modified query inputs relabeled the old result.");

            using (var editor = new DeviceConfigurationDialog(_configuration.Devices.Single(device => device.Id == a.Id), _drivers))
            {
                editor.Show(this);
                try
                {
                    editor.SetFieldForSmoke("Name", "");
                    var rejected = false;
                    try { await editor.ApplyAndAcceptForSmokeAsync(); }
                    catch (InvalidOperationException) { rejected = true; }
                    var name = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "DeviceConfiguration_Name");
                    var feedback = Descendants(editor).Single(control => control.Name == "EditorValidationMessage");
                    CheckUsability(rejected && editor.Visible && name.Text.Length == 0 && name.ContainsFocus && feedback.Visible,
                        "invalid name did not retain the draft and focus the failing field.");
                    AssertVisibleBounds(feedback, "inline validation feedback");
                    foreach (var command in editor.SmokeCommands) AssertVisibleBounds(command, "validation recovery action");
                }
                finally { editor.Close(); }
            }
            using (var editor = new WriteValueDialog(a.Name, a.Tags[0].Name, "℃", "42.5", TagDataType.Float64))
            {
                editor.Show(this);
                try
                {
                    var relative = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "WriteRelativeTolerance");
                    relative.Text = "2";
                    ((Button)editor.AcceptButton!).PerformClick();
                    CheckUsability(editor.Visible && relative.ContainsFocus
                        && Descendants(editor).Single(control => control.Name == "WriteValidationMessage").Visible,
                        "invalid write tolerance did not remain editable in the window.");
                    relative.Text = "0.01"; ((Button)editor.AcceptButton!).PerformClick();
                    CheckUsability(editor.DialogResult == DialogResult.OK && editor.Value == "42.5"
                        && editor.VerificationOptions.RelativeTolerance == .01, "corrected write options were not accepted.");
                }
                finally { if (!editor.IsDisposed) editor.Close(); }
            }
            await AssertSavedConfigurationForSmokeAsync("usability commands");
            Console.WriteLine($"Usability smoke passed at actual {DeviceDpi} DPI: native menus and keyboard opening; captured device identity; point search/selection; stable live scroll; four platform tabs and reset; executed-query labeling; inline error focus and recovery. Only isolated UI fixtures were used.");
        }
        finally
        {
            _pointsDialog.Hide();
            _deviceMore.OpenedMenu.Close(); _tagMore.OpenedMenu.Close();
            _deviceSearch.Clear(); _tagSearch.Clear();
            await RestoreConfigurationForSmokeAsync(original);
            RebindConfigurationViews(originalSelection); ShowPage(page); UpdateCommands();
        }
    }

    private async Task SmokePointDialogRowAsync(DeviceConfiguration selected, DeviceConfiguration target)
    {
        Exception? failure = null;
        var observed = false;
        var targetRow = _deviceGrid.Rows.Cast<DataGridViewRow>().Single(row => ((DeviceRow)row.DataBoundItem).Id == target.Id);
        using var timer = new System.Windows.Forms.Timer { Interval = 40 };
        timer.Tick += async (_, _) =>
        {
            if (!_pointsDialog.Visible || !_pointsDialog.Modal) return;
            timer.Stop(); observed = true;
            try
            {
                CheckUsability(SelectedDevice()?.Id == selected.Id && PointDevice()?.Id == target.Id,
                    "point dialog followed selection instead of the clicked row.");
                _tagSearch.Text = "B 点位";
                RebindConfigurationViews(selected.Id);
                CheckUsability(PointDevice()?.Id == target.Id && _tagGrid.Rows.Count == 1 && _tagSearch.Text == "B 点位",
                    "refresh changed the point dialog target or search.");
                _tagSearch.Clear();
                await EditPointFromDialogForSmokeAsync(_pointsDialog.AddTagButton, "弹窗新增点位", rejectFirst: true);
                var added = _configuration.Devices.Single(device => device.Id == target.Id).Tags.Single(tag => tag.Name == "弹窗新增点位");
                CheckUsability(SelectedTag()?.Id == added.Id, "new point was not selected after saving.");
                await EditPointFromDialogForSmokeAsync(_pointsDialog.EditTagButton, "弹窗编辑点位", rejectFirst: false);
                CheckUsability(_configuration.Devices.Single(device => device.Id == target.Id).Tags.Single(tag => tag.Id == added.Id).Name == "弹窗编辑点位"
                    && _configuration.Devices.Single(device => device.Id == selected.Id).Tags.Count == selected.Tags.Count,
                    "point dialog saved to the wrong device or replaced point identity.");
                SaveEditorScreenshot(_pointsDialog, "points", "saved");
                var fullSize = _pointsDialog.ClientSize;
                _pointsDialog.ClientSize = new Size(UiTheme.Scale(_pointsDialog, 760), UiTheme.Scale(_pointsDialog, 460));
                PerformLayoutRecursively(_pointsDialog);
                BringIntoViewAndAssert(_pointsDialog.AddTagButton, "compact point dialog add");
                BringIntoViewAndAssert(_tagSearch, "compact point dialog search");
                BringIntoViewAndAssert(Descendants(_pointsDialog).Single(control => control.Name == "ClosePoints"), "compact point dialog close");
                VerifyPointRowsForSmoke("compact point dialog");
                SaveEditorScreenshot(_pointsDialog, "points", "compact");
                _pointsDialog.ClientSize = fullSize;
            }
            catch (Exception exception) { failure = exception; TrySaveSmokeFailure(_pointsDialog, "point-dialog-failed"); }
            finally { _pointsDialog.DialogResult = DialogResult.Cancel; }
        };
        timer.Start();
        try { await DeviceGridActionAsync(new DataGridViewCellEventArgs(_deviceGrid.Columns[DevicePointsColumnName].Index, targetRow.Index)); }
        finally { timer.Stop(); }
        if (failure is not null) throw new InvalidOperationException("Point dialog row action failed.", failure);
        CheckUsability(observed && !_pointsDialog.Visible && _pointsDeviceId is null, "point dialog did not release its device context.");
        Console.WriteLine("Point dialog row action passed: clicked B while A selected, pinned context through refresh, nested add/edit and validation retention, stable point ID and compact layout.");
    }

    private async Task EditPointFromDialogForSmokeAsync(AntdUI.Button action, string name, bool rejectFirst)
    {
        Exception? failure = null;
        var observed = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 40 };
        timer.Tick += async (_, _) =>
        {
            var editor = System.Windows.Forms.Application.OpenForms.OfType<TagEditor>().SingleOrDefault(form => form.Visible && form.Modal);
            if (editor is null) return;
            timer.Stop(); observed = true;
            try
            {
                CheckUsability(ReferenceEquals(editor.Owner, _pointsDialog), "point editor does not belong to the point dialog.");
                var field = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "TagName");
                if (rejectFirst)
                {
                    field.Text = "";
                    try { await editor.ApplyAndAcceptForSmokeAsync(); }
                    catch (Exception) when (editor.LastApplyErrorForSmoke is not null) { }
                    CheckUsability(editor.Visible && editor.LastApplyErrorForSmoke is not null && field.Text == "",
                        "invalid point input did not remain in the nested editor.");
                }
                field.Text = name;
                await editor.ApplyAndAcceptForSmokeAsync();
            }
            catch (Exception exception) { failure = exception; }
            finally { editor.DialogResult = DialogResult.Cancel; editor.Close(); }
        };
        timer.Start();
        try { action.PerformClick(); }
        finally { timer.Stop(); }
        if (failure is not null) throw new InvalidOperationException("Nested point editor failed.", failure);
        CheckUsability(observed, "point action did not open an editor.");
        await Task.CompletedTask;
    }

    private static void CheckUsability(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Usability smoke: " + message);
    }

    private void SmokeListSpace()
    {
        var originalSize = ClientSize;
        try
        {
            CheckUsability(!Descendants(_devicesPageView).Contains(_tagGrid), "point grid remains on the device page.");
            CheckUsability(_deviceGrid.ClientSize.Height >= _deviceGrid.ColumnHeadersHeight + _deviceGrid.RowTemplate.Height * 5,
                "device list must offer at least five full rows.");
            SaveEditorScreenshot(this, "skin", "lists-adjusted");
            ClientSize = new Size(originalSize.Width, Math.Min(originalSize.Height, UiTheme.Scale(this, 600)));
            PerformLayoutRecursively(this);
            BringIntoViewAndAssert(_devicesPageView.AddDeviceButton, "short window device actions");
            BringIntoViewAndAssert(_deviceSearch, "short window device search");
            CheckUsability(_deviceGrid.ClientSize.Height >= _deviceGrid.ColumnHeadersHeight + _deviceGrid.RowTemplate.Height * 3,
                "short window collapsed the device list.");
            SaveEditorScreenshot(this, "skin", "short-window");
            _pointsDialog.Show(this);
            PerformLayoutRecursively(_pointsDialog);
            BringIntoViewAndAssert(_pointsDialog.AddTagButton, "point dialog actions");
            BringIntoViewAndAssert(_tagSearch, "point dialog search");
            CheckUsability(_tagGrid.ClientSize.Height >= _tagGrid.ColumnHeadersHeight + _tagGrid.RowTemplate.Height * 5,
                "point dialog list is too short.");
            SaveEditorScreenshot(_pointsDialog, "points", "dialog");
            Console.WriteLine($"Skin list layout passed at actual {DeviceDpi} DPI: device-only full-height grid, separate point dialog and short-window actions reachable.");
        }
        finally
        {
            _pointsDialog.Hide();
            ClientSize = originalSize;
            PerformLayoutRecursively(this);
            ResetScrollPositions(_pages["devices"], navigationMenu);
        }
    }

    private static AntdUI.ContextMenuStripItem MenuItem(CommandMenuButton button, string key) =>
        button.OpenedItems.Single(item => item.ID == key);

    private static void OpenMenuForSmoke(CommandMenuButton button, string context, bool keyboard = false)
    {
        BringIntoViewAndAssert(button, context);
        if (keyboard) button.OpenByKeyboardForSmoke(); else button.PerformClick();
        var menu = button.OpenedMenu;
        menu.PerformLayout();
        var bounds = menu is AntdUI.ILayeredForm layered ? layered.TargetRect : menu.Bounds;
        CheckUsability(button.ShowArrow && menu.Visible && button.OpenedItems.Count > 0
            && Screen.FromControl(button).WorkingArea.Contains(bounds), context + ": official menu or arrow is missing/clipped.");
    }

    private static void SendMenuKeyForSmoke(CommandMenuButton button, Keys key)
    {
        if (button.OpenedMenu is not AntdUI.ILayeredForm layered || layered.KeyCall?.Invoke(key) is not true)
            throw new InvalidOperationException("Official menu did not handle " + key);
    }

    private void SaveUsabilityMenuScreenshot(Form menu) => SaveEditorScreenshot(menu, "usability", "menu");
}
