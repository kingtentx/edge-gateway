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
            var configure = MenuItem(_deviceMore, "ConfigureDevice"); configure.Select();
            CheckUsability(configure.Selected && configure.Enabled, "native menu item cannot be selected.");
            SaveUsabilityMenuScreenshot(_deviceMore.OpenedMenu);
            // The open menu owns A's identity even if a background rebind changes
            // selection to B before activation.
            RebindConfigurationViews(b.Id);
            await SmokeDeviceConfigurationModalAsync(() => { configure.PerformClick(); return Task.CompletedTask; }, editor =>
            {
                var name = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "DeviceConfiguration_Name");
                CheckUsability(name.Text == a.Name, "an opened menu redirected its target to a later selection.");
                editor.SetFieldForSmoke("Group", "从菜单保存");
            }, "captured menu device identity");
            _deviceMore.OpenedMenu.Close();
            CheckUsability(_configuration.Devices.Single(device => device.Id == a.Id).Group == "从菜单保存"
                && _configuration.Devices.Single(device => device.Id == b.Id).Group == b.Group, "menu edited another device.");

            OpenMenuForSmoke(_channelMenu, "channel menu");
            CheckUsability(MenuItem(_channelMenu, "AddChannel").Enabled && !MenuItem(_channelMenu, "EditChannel").Enabled,
                "channel menu availability does not match its target.");
            _channelMenu.OpenedMenu.Close();
            OpenMenuForSmoke(_tagMore, "point menu");
            CheckUsability(MenuItem(_tagMore, "ImportTags").Enabled && MenuItem(_tagMore, "TagTemplate").Enabled,
                "point import/template commands are missing.");
            _tagMore.OpenedMenu.Close();

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
            ResetScrollPositions(_pages["cloud"], _navigation["overview"]);
            SaveEditorScreenshot(this, "usability", "platform");

            ShowPage("realtime"); SelectChoice(_realtimeDevice, a.Id);
            var timestamp = DateTimeOffset.UtcNow;
            var samples = a.Tags.Select((tag, index) => new TelemetrySample(a.Id, a.Name, tag.Id, tag.Name,
                tag.Address, (double)index, (double)index, SampleQuality.Good, timestamp, tag.Unit)).ToList();
            RefreshRealtime(samples);
            _realtimeGrid.CurrentCell = _realtimeGrid.Rows[20].Cells["Error"];
            var before = GridViewState.Capture(_realtimeGrid, row => ((SampleRow)row).Sample.TagId);
            RefreshRealtime(samples.Select(sample => sample with { Value = 42D, Timestamp = timestamp.AddSeconds(1) }).ToList());
            var after = GridViewState.Capture(_realtimeGrid, row => ((SampleRow)row).Sample.TagId);
            CheckUsability(before.SelectedKey!.Equals(after.SelectedKey) && before.ColumnName == after.ColumnName
                && before.FirstVisibleIndex == after.FirstVisibleIndex && before.HorizontalOffset == after.HorizontalOffset,
                "live refresh changed the operator's row/column/scroll position.");

            ShowPage("history"); SelectChoice(_historyDevice, a.Id); SelectChoice(_historyTag, a.Tags[0].Id);
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
            _deviceMore.OpenedMenu.Close(); _channelMenu.OpenedMenu.Close(); _tagMore.OpenedMenu.Close();
            _deviceSearch.Clear(); _tagSearch.Clear();
            await RestoreConfigurationForSmokeAsync(original);
            RebindConfigurationViews(originalSelection); ShowPage(page); UpdateCommands();
        }
    }

    private static void CheckUsability(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Usability smoke: " + message);
    }

    private void SmokeListSpace()
    {
        var originalSize = ClientSize;
        var split = _devicesPageView.ListSplit;
        var originalDistance = split.SplitterDistance;
        var selectedDevice = SelectedDevice()?.Id;
        var selectedTag = SelectedTag()?.Id;
        try
        {
            CheckUsability(_deviceGrid.ClientSize.Height >= _deviceGrid.ColumnHeadersHeight + _deviceGrid.RowTemplate.Height * 3
                && _tagGrid.ClientSize.Height >= _tagGrid.ColumnHeadersHeight + _tagGrid.RowTemplate.Height * 3,
                "default lists must each offer at least three full rows.");
            var target = Math.Clamp(originalDistance + UiTheme.Scale(this, 48), split.Panel1MinSize,
                split.Height - split.SplitterWidth - split.Panel2MinSize);
            // Exercise the same public WinForms events used by a native splitter drag.
            split.OnSplitterMoving(new SplitterCancelEventArgs(1, target, 1, target));
            split.SplitterDistance = target;
            PerformLayoutRecursively(this);
            CheckUsability(split.SplitterDistance == target && target > originalDistance, "drag did not enlarge the device list.");
            RebindConfigurationViews(selectedDevice);
            PerformLayoutRecursively(this);
            CheckUsability(split.SplitterDistance == target && SelectedDevice()?.Id == selectedDevice && SelectedTag()?.Id == selectedTag,
                "refresh lost the divider position or row identity.");
            SaveEditorScreenshot(this, "skin", "lists-adjusted");
            ClientSize = new Size(originalSize.Width, Math.Min(originalSize.Height, UiTheme.Scale(this, 600)));
            PerformLayoutRecursively(this);
            BringIntoViewAndAssert(_devicesPageView.AddDeviceButton, "short window device actions");
            BringIntoViewAndAssert(_devicesPageView.AddTagButton, "short window point actions");
            BringIntoViewAndAssert(_tagSearch, "short window point search");
            CheckUsability(_tagGrid.ClientSize.Height >= _tagGrid.ColumnHeadersHeight + _tagGrid.RowTemplate.Height * 2,
                "short window collapsed the point list.");
            SaveEditorScreenshot(this, "skin", "short-window");
            Console.WriteLine($"Skin list layout passed at actual {DeviceDpi} DPI: both grids >= 3 initial rows; divider adjustment and refresh retain selection; short window actions reachable.");
        }
        finally
        {
            ClientSize = originalSize;
            typeof(SplitContainer).GetMethod("OnDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(split, [EventArgs.Empty]);
            PerformLayoutRecursively(this);
            ResetScrollPositions(_pages["devices"], _navigation["devices"]);
        }
    }

    private static ToolStripMenuItem MenuItem(CommandMenuButton button, string key) =>
        button.OpenedMenu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == key);

    private static void OpenMenuForSmoke(CommandMenuButton button, string context, bool keyboard = false)
    {
        BringIntoViewAndAssert(button, context);
        if (keyboard) button.OpenByKeyboardForSmoke(); else button.PerformClick();
        var menu = button.OpenedMenu;
        menu.PerformLayout();
        CheckUsability(menu.Visible && menu.Items.Count > 0 && Screen.FromControl(menu).WorkingArea.Contains(menu.Bounds),
            context + ": the native menu is not fully visible on its monitor.");
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
            CheckUsability(item.Available && item.Bounds.Width > 0 && item.Bounds.Height > 0, context + ": a command has no visible item.");
    }

    private void SaveUsabilityMenuScreenshot(ContextMenuStrip menu)
    {
        if (string.IsNullOrWhiteSpace(_smokeScreenshotPath)) return;
        var path = Path.GetFullPath(_smokeScreenshotPath);
        var output = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-usability-menu.png");
        using var image = new Bitmap(menu.Width, menu.Height);
        menu.DrawToBitmap(image, menu.ClientRectangle);
        image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
    }
}
