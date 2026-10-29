using EdgeGateway.Model;
using EdgeGateway.Application;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private static readonly List<string> SmokeFocusRecoveries = [];

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private async Task RunLayoutMatrixAsync()
    {
        if (_application.IsRunning) throw new InvalidOperationException("Layout verification must never run device collection.");
        var directory = SmokeOutputDirectory("layout-matrix");
        Directory.CreateDirectory(directory);
        var results = new List<LayoutCaseResult>();
        var actualDpi = DeviceDpi;
        var nativeDpi = GetDpiForWindow(Handle);
        if (nativeDpi != actualDpi) throw new InvalidOperationException($"DPI mismatch: DeviceDpi={actualDpi}, GetDpiForWindow={nativeDpi}.");
        var screen = Screen.FromControl(this);
        var originalBounds = Bounds;
        var originalMinimum = MinimumSize;
        var originalMaximum = MaximumSize;
        var resolutions = new[] { new Size(1366, 768), new Size(1920, 1080), new Size(2560, 1440), new Size(3200, 2000) };
        var percentages = new[] { 100, 125, 150, 175, 200 };
        using var focusDiagnostics = StartSmokeFocusDiagnostics(directory);
        try
        {
            foreach (var resolution in resolutions)
            foreach (var percentage in percentages)
            {
                // The host monitor's actual DPI never changes. Reproduce the logical
                // space offered by each nominal resolution/scale pair, reserving a
                // 48-logical-pixel taskbar; this is deliberately NOT a DPI-change test.
                var nominalDpi = 96 * percentage / 100;
                var logicalWidth = resolution.Width * 96d / nominalDpi;
                var logicalHeight = resolution.Height * 96d / nominalDpi - 48;
                var testArea = new Rectangle(screen.WorkingArea.Location,
                    new Size((int)Math.Round(logicalWidth * actualDpi / 96d), (int)Math.Round(logicalHeight * actualDpi / 96d)));
                var name = $"{resolution.Width}x{resolution.Height}-{percentage}pct";
                var result = new LayoutCaseResult
                {
                    Name = name, PhysicalResolution = resolution, NominalScalePercent = percentage,
                    NominalDpi = nominalDpi, ActualDeviceDpi = actualDpi, NativeWindowDpi = nativeDpi,
                    EquivalentLogicalWorkArea = new SizeF((float)logicalWidth, (float)logicalHeight),
                    RequestedWindowBounds = testArea
                };
                results.Add(result);
                try
                {
                    focusDiagnostics?.Capture(name + " before window resize");
                    MinimumSize = Size.Empty;
                    // Form.SetBoundsCore clamps ordinary Size assignments to the host's
                    // MaxWindowTrackSize, even in a viewport-only test. Resize this test
                    // HWND through the public Win32 API, retaining the real DPI; capture
                    // and assert the resulting actual bounds instead of assuming success.
                    MaximumSize = testArea.Size;
                    if (!SetWindowPos(Handle, IntPtr.Zero, testArea.X, testArea.Y, testArea.Width, testArea.Height, 0x0014))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not resize the layout-test window.");
                    FitToWorkingArea(testWorkArea: testArea);
                    await Task.Yield();
                    PerformLayoutRecursively(this);
                    focusDiagnostics?.Capture(name + " after window resize/layout");
                    result.ActualWindowBounds = Bounds;
                    if (Bounds != testArea)
                        throw new InvalidOperationException($"{name}: requested equivalent viewport {testArea}, but Windows produced {Bounds}.");
                    if (DeviceDpi != actualDpi || GetDpiForWindow(Handle) != nativeDpi)
                        throw new InvalidOperationException($"{name}: actual DPI changed during a viewport-only test.");
                    AssertHeaderStatusFits();
                    foreach (var navigation in _navigation.Values)
                    {
                        navigationMenu.Focus(navigation, true);
                        AssertVisibleBounds(navigationMenu, name + " navigation");
                        AssertNavigationItemFits(navigation, name);
                    }
                    foreach (var key in _pages.Keys)
                    {
                        if (key == "devices") focusDiagnostics?.Capture(name + " before devices ShowPage");
                        ShowPage(key);
                        if (key == "devices") focusDiagnostics?.Capture(name + " after devices ShowPage");
                        PerformLayoutRecursively(this);
                        if (key == "devices") focusDiagnostics?.Capture(name + " after devices layout");
                        AssertHeaderStatusFits();
                        AssertPageToolbarsFit(_pages[key], name + " " + key);
                        if (key == "overview") VerifyOverviewCards(name);
                        if (key == "devices")
                        {
                            VerifyDeviceRowCommandsForSmoke(name);
                            VerifyPointRowsForSmoke(name);
                        }
                        if (key == "cloud")
                            foreach (var field in _cloudFields.Values)
                                BringIntoViewAndAssert(field, name + " cloud field");
                        if (key == "database")
                        {
                            foreach (TabPage tab in _databaseTabs.TabPages)
                            {
                                _databaseTabs.SelectedTab = tab; PerformLayoutRecursively(this);
                                AssertPageToolbarsFit(tab, name + " database " + tab.Text);
                                foreach (var button in Descendants(tab).OfType<Button>())
                                    BringIntoViewAndAssert(button, name + " database command " + button.Text);
                            }
                            _databaseTabs.SelectedIndex = 0;
                        }
                        if (key == "logs")
                        {
                            foreach (var field in _loggingFields.Values)
                                BringIntoViewAndAssert(field, name + " logging field " + field.Name);
                            foreach (var command in new Control[] { _loggingApply, _loggingReset })
                                BringIntoViewAndAssert(command, name + " logging command");
                            BringIntoViewAndAssert(Descendants(_pages["logs"]).Single(control => control.Name == "OpenLogDirectory"), name + " log directory command");
                            BringIntoViewAndAssert(_loggingFileState, name + " log file status");
                            result.LoggingInputMetrics = _loggingFields.Values.Select(ControlMetric.From).ToList();
                            _logTabs.SelectedTab = _loggingPageView.SettingsTab;
                        }
                        ResetScrollPositions(_pages[key], navigationMenu);
                        result.CheckedPages.Add(key);
                        if (key == "overview")
                        {
                            // Preserve the native state immediately around WM_PRINT. Do not pump or lay out between these snapshots.
                            focusDiagnostics?.Capture(name + " before overview screenshot");
                            SaveSmokeScreenshot(this, Path.Combine(directory, name + "-overview.png"));
                            focusDiagnostics?.Capture(name + " after overview screenshot");
                        }
                        if (key == "devices" && name == "1920x1080-125pct")
                            SaveSmokeScreenshot(this, Path.Combine(directory, name + "-devices.png"));
                    }
                    result.HeaderMetrics = new Control[] { _runState, _savedState, _automaticSaveState }.Select(ControlMetric.From).ToList();
                    focusDiagnostics?.Capture(name + " before matrix dialogs");
                    VerifyMatrixDialogs(testArea, directory, result);
                    VerifyDatabaseMatrixDialogs(testArea, directory, result);
                    if (resolution == new Size(1366, 768) && percentage == 200)
                        VerifyMinimumConfigurationDialog(testArea, directory, result);
                    focusDiagnostics?.Capture(name + " after matrix dialogs");
                    result.Passed = true;
                    Console.WriteLine($"Layout viewport passed: {name}; nominal DPI {nominalDpi}, actual DPI {actualDpi}; viewport {Bounds.Width}x{Bounds.Height}.");
                }
                catch (Exception exception)
                {
                    result.Error = exception.ToString();
                    focusDiagnostics?.Capture(name + " matrix failure before failure screenshot", failure: true);
                    TrySaveSmokeScreenshot(this, Path.Combine(directory, name + "-failed.png"));
                    throw;
                }
                finally { WriteLayoutReport(directory, results, actualDpi, nativeDpi); }
            }
        }
        finally
        {
            MinimumSize = Size.Empty;
            MaximumSize = originalMaximum;
            Bounds = originalBounds;
            MinimumSize = originalMinimum;
            FitToWorkingArea();
            WriteLayoutReport(directory, results, actualDpi, nativeDpi);
        }
        Console.WriteLine($"Layout matrix passed: {results.Count} equivalent viewport cases at actual {actualDpi} DPI; real multi-DPI/multi-monitor tests remain separate.");
    }

    private void VerifyMatrixDialogs(Rectangle area, string directory, LayoutCaseResult result)
    {
        var descriptor = _drivers.Drivers.Single(x => x.Id == "PLC.ModBusMaster");
        var device = new DeviceConfiguration { Name = "布局检查 · Modbus", DriverId = descriptor.Id, Enabled = false };
        using (var editor = new DeviceConfigurationDialog(device, _drivers, isNew: true))
        {
            editor.Show(this);
            try
            {
                FitSmokeDialog(editor, area);
                editor.VerifyBindingsForSmoke();
                VerifyEditorLayout(editor, result.Name + " device");
                result.DeviceParameterMetrics = editor.SmokeParameterInputs.Select(ControlMetric.From).ToList();
                SaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-modbus-device.png"));
                result.CheckedDialogs.Add("PLC.ModBusMaster device");
            }
            catch { TrySaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-device-failed.png")); throw; }
            finally { editor.Close(); }
        }
        using (var editor = new TagEditor(TagEditor.CreateDefaultTag(descriptor, "布局检查点位"), descriptor))
        {
            editor.Show(this);
            try
            {
                FitSmokeDialog(editor, area);
                editor.VerifyBindingsForSmoke();
                VerifyEditorLayout(editor, result.Name + " tag");
                result.CheckedDialogs.Add("PLC.ModBusMaster tag");
            }
            catch { TrySaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-tag-failed.png")); throw; }
            finally { editor.Close(); }
        }
        using (var editor = new WriteValueDialog("布局验证设备", "字符串写入点位", "", "保留完整文本及前后空白", TagDataType.Utf8String))
        {
            editor.Show(this);
            try
            {
                FitSmokeDialog(editor, area);
                foreach (var command in editor.SmokeCommands) AssertVisibleBounds(command, result.Name + " write action");
                if (editor.SmokeCommands[0].Left >= editor.SmokeCommands[1].Left)
                    throw new InvalidOperationException("Write confirm must precede cancel from left to right.");
                BringIntoViewAndAssert(editor.SmokeValueInput, result.Name + " write value");
                foreach (var input in editor.SmokeVerificationInputs)
                    BringIntoViewAndAssert(input, result.Name + " write verification");
                result.WriteInputMetric = ControlMetric.From(editor.SmokeValueInput);
                result.WriteVerificationMetrics = editor.SmokeVerificationInputs.Select(ControlMetric.From).ToList();
                result.CheckedDialogs.Add("WriteValueDialog multiline");
            }
            catch { TrySaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-write-failed.png")); throw; }
            finally { editor.Close(); }
        }
        using (var editor = new TagImportDialog("点位导入布局检查", TagCsvExchange.Export(new[]
               { new TagConfiguration { Name = "批量维护点位", Address = "0" } }), [], _ => []))
        {
            editor.Show(this);
            try
            {
                FitSmokeDialog(editor, area);
                VerifyEditorLayout(editor, result.Name + " tag import");
                result.CheckedDialogs.Add("TagImportDialog preview");
            }
            catch { TrySaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-tag-import-failed.png")); throw; }
            finally { editor.Close(); }
        }
        using (var editor = new ChannelEditor(new ChannelConfiguration
               { Name = "布局验证通道", InterRequestDelayMs = 25, Description = "同一通道通信串行，不同通道并行。" }))
        {
            editor.Show(this);
            try
            {
                FitSmokeDialog(editor, area);
                VerifyEditorLayout(editor, result.Name + " channel");
                var inputs = Descendants(editor).Where(control => control.Name is "ChannelName" or "ChannelDelay" or "ChannelDescription").ToArray();
                if (inputs.Length != 3) throw new InvalidOperationException(result.Name + ": channel editor is missing an input.");
                foreach (var input in inputs) BringIntoViewAndAssert(input, result.Name + " channel field");
                result.ChannelInputMetrics = inputs.Select(ControlMetric.From).ToList();
                SaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-channel.png"));
                result.CheckedDialogs.Add("ChannelEditor");
            }
            catch { TrySaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-channel-failed.png")); throw; }
            finally { editor.Close(); }
        }
    }

    private void VerifyDatabaseMatrixDialogs(Rectangle area, string directory, LayoutCaseResult result)
    {
        var connection = RecordingConnectionFixture();
        using (var editor = new DatabaseConnectionDialog(connection, _application))
        {
            editor.Show(this);
            try
            {
                FitSmokeDialog(editor, area); VerifyEditorLayout(editor, result.Name + " database connection");
                foreach (var input in editor.SmokeInputs) BringIntoViewAndAssert(input, result.Name + " database " + input.Name);
                foreach (var button in Descendants(editor).OfType<Button>().Where(button => button.Name is not ("EditorHelp" or "EditorValidationLocate") || button.Visible)) BringIntoViewAndAssert(button, result.Name + " database connection command");
                result.DatabaseConnectionMetrics = editor.SmokeInputs.Select(ControlMetric.From).ToList();
                result.CheckedDialogs.Add("DatabaseConnectionDialog");
            }
            catch { TrySaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-database-connection-failed.png")); throw; }
            finally { editor.Close(); }
        }
        var device = new DeviceConfiguration { Name = "字段布局验证设备", DriverId = "simulator",
            Tags = [new() { Name = "温度", Address = "constant:25", DataType = TagDataType.Float64 },
                    new() { Name = "流量", Address = "ramp", DataType = TagDataType.Float64 }] };
        var group = RecordingMappingFixture(connection.Id, device.Tags[0].Id); group.TagIds = [device.Tags[0].Id];
        using (var editor = new DatabaseWriteGroupDialog(group, [connection], [device], _application))
        {
            editor.LoadColumnsForSmoke(RecordingColumnFixtures()); editor.Show(this);
            try
            {
                FitSmokeDialog(editor, area); VerifyEditorLayout(editor, result.Name + " database write group");
                // The switch-to-custom button only exists visually in standard mode.
                // Visit that mode before checking every command, then restore the mapping fixture.
                editor.SelectModeForSmoke(DatabaseStorageMode.PointRows);
                foreach (var button in Descendants(editor).OfType<Button>().Where(button => button.Name is not ("EditorHelp" or "EditorValidationLocate") || button.Visible)) BringIntoViewAndAssert(button, result.Name + " database write-group command");
                editor.SelectModeForSmoke(group.Mode);
                VerifyDatabasePointSelectionForSmoke(editor, result.Name + " database point selection");
                result.DatabasePointSelectionVerified = true;
                result.DatabaseMappingCellsChecked = VerifyDatabaseMappingForSmoke(editor, result.Name + " database column mapping");
                editor.SmokeMapping.CurrentCell = editor.SmokeMapping.Rows[1].Cells["Column"];
                SaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-database-write-group.png"));
                result.CheckedDialogs.Add("DatabaseWriteGroupDialog");
            }
            catch { TrySaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-database-write-group-failed.png")); throw; }
            finally { editor.Close(); }
        }
    }

    private static void FitSmokeDialog(DpiAwareForm editor, Rectangle area)
    {
        editor.FitToWorkingArea(center: true, testWorkArea: area);
        PerformLayoutRecursively(editor);
        if (!area.Contains(editor.Bounds)) throw new InvalidOperationException($"{editor.Text}: dialog exceeds equivalent work area {area}: {editor.Bounds}.");
    }

    private void SmokeDynamicDriverSwitch()
    {
        var device = new DeviceConfiguration { Name = "显示后驱动切换检查", DriverId = "simulator", Enabled = false };
        var before = ConfigurationSmokeJson(device);
        using var editor = new DeviceConfigurationDialog(device, _drivers, isNew: true);
        editor.Show(this);
        try
        {
            var common = new Dictionary<string, string>
            {
                ["Name"] = "切换后保留公共配置", ["Group"] = "共享字段验证分组", ["Enabled"] = "true",
                ["PollIntervalMs"] = "2190", ["TimeoutMs"] = "5430"
            };
            foreach (var field in common) editor.SetFieldForSmoke(field.Key, field.Value);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var driverId in new[] { "PLC.ModBusMaster", "OPC.UaClient", "modbus-rtu", "PLC.ModBusMaster", "OPC.UaClient", "modbus-rtu" })
            {
                editor.SelectDriverForSmoke(driverId);
                var (key, value, inputName) = driverId switch
                {
                    "PLC.ModBusMaster" => ("Properties.SlaveAddress", "9", "DriverParameter_SlaveAddress"),
                    "OPC.UaClient" => ("Properties.Uri", "opc.tcp://192.0.2.42:4840", "DriverParameter_Uri"),
                    _ => ("SerialPortName", "COM19", "DeviceConfiguration_SerialPortName")
                };
                if (visited.Add(driverId)) editor.SetFieldForSmoke(key, value);
                else AssertConfigurationSmokeInput(editor.SmokeParameterInputs.Single(x => x.Name == inputName), value, driverId + " restored draft");
                foreach (var field in common)
                    AssertConfigurationSmokeInput(Descendants(editor).Single(x => x.Name == "DeviceConfiguration_" + field.Key),
                        field.Value, driverId + " common draft");
                editor.VerifyBindingsForSmoke();
                VerifyEditorLayout(editor, "dynamic " + driverId);
                foreach (var input in editor.SmokeParameterInputs)
                {
                    if (input.DeviceDpi != editor.DeviceDpi)
                        throw new InvalidOperationException($"Dynamic parameter {input.Name}: DPI {input.DeviceDpi} differs from editor {editor.DeviceDpi}.");
                    if (input.Font.SizeInPoints < 9.9F)
                        throw new InvalidOperationException($"Dynamic parameter {input.Name}: font unexpectedly shrank to {input.Font.SizeInPoints} pt.");
                }
            }
            if (editor.Value is not null) throw new InvalidOperationException("Changing drivers committed an unconfirmed device draft.");
            editor.SelectDriverForSmoke("modbus-tcp");
            editor.SetFieldForSmoke("Host", "192.0.2.43");
            var added = editor.CommitForSmoke();
            if (added.DriverId != "modbus-tcp" || added.Host != "192.0.2.43" || added.Tags.Count != 1)
                throw new InvalidOperationException("Adding a device did not use the last selected driver and its default point.");
            AssertConfigurationSmokeUnchanged(before, device, "dynamic driver draft isolation");
            Console.WriteLine($"Dynamic driver switch passed at actual {editor.DeviceDpi} DPI: native and migrated drafts survived switching back, no unconfirmed result escaped, and add used the final driver.");
        }
        catch { TrySaveSmokeFailure(editor, "dynamic-driver-switch-failed"); throw; }
        finally { editor.Close(); }
    }

    private void VerifyOverviewCards(string context)
    {
        var cards = Descendants(_pages["overview"]).Where(x => x.Name.StartsWith("OverviewCard_", StringComparison.Ordinal)).ToArray();
        if (cards.Length != 4) throw new InvalidOperationException("Overview must retain all four live status cards.");
        if (Descendants(_pages["overview"]).OfType<GatewayTable>().Any())
            throw new InvalidOperationException("Overview must contain device summaries, not device lists.");
        for (var i = 0; i < cards.Length; i++)
        {
            if (cards[i].Width <= 0 || cards[i].Height <= 0) throw new InvalidOperationException(context + ": empty overview card.");
            for (var j = i + 1; j < cards.Length; j++)
                if (cards[i].Bounds.IntersectsWith(cards[j].Bounds)) throw new InvalidOperationException(context + ": overview cards overlap.");
            foreach (var label in cards[i].Controls.OfType<Label>())
                BringIntoViewAndAssert(label, context + " card text");
        }
        foreach (var name in new[] { "OverviewCollectionHealth", "OverviewDeliveryHealth", "OverviewServerResources", "OverviewCpuCard", "OverviewMemoryCard" })
        {
            var panel = Descendants(_pages["overview"]).Single(control => control.Name == name);
            var labels = name is "OverviewCpuCard" or "OverviewMemoryCard"
                ? Descendants(panel).OfType<Label>() : panel.Controls.OfType<Label>();
            foreach (var label in labels) BringIntoViewAndAssert(label, context + " " + name);
        }
    }

    private void VerifyPointRowsForSmoke(string context, bool verifyInitialViewport = false)
    {
        var openedHere = !_pointsDialog.Visible;
        if (openedHere) { _pointsDialog.Show(this); PerformLayoutRecursively(_pointsDialog); }
        var grid = _tagGrid;
        var state = GridViewState.Capture(grid, item => ((TagRow)item).Id);
        var priorCell = grid.CurrentCell;
        var selectedRows = grid.SelectedRows.Cast<GatewayTableRow>().ToArray();
        var priorFocus = Descendants(this).FirstOrDefault(control => control.Focused);
        var viewports = new List<(ScrollableControl Viewport, Point Position)>();
        for (var parent = grid.Parent; parent is not null; parent = parent.Parent)
            if (parent is ScrollableControl { AutoScroll: true } scroll)
                viewports.Add((scroll, new Point(-scroll.AutoScrollPosition.X, -scroll.AutoScrollPosition.Y)));
        try
        {
            if (grid.Rows.Count == 0 || grid.Columns["Name"] is not { Visible: true } nameColumn)
                throw new InvalidOperationException(context + ": no visible point-name cell is available to verify.");
            var first = grid.Rows[0].Cells[nameColumn.Index];
            // The ordinary baseline has room for both tables. Check its first point
            // before focusing or scrolling, so the test cannot hide a tall empty header.
            // A genuinely small/high-DPI workspace still verifies actual reachability below.
            if (verifyInitialViewport && ClientSize.Width >= UiTheme.Scale(this, 960)
                && ClientSize.Height >= UiTheme.Scale(this, 680))
                AssertCellVisible(first, requireFocus: false, "initial viewport without outer scrolling");

            var lastColumn = grid.Columns.Cast<GatewayTableColumn>().Where(column => column.Visible)
                .MaxBy(column => column.DisplayIndex)!;
            var last = grid.Rows[grid.Rows.Count - 1].Cells[lastColumn.Index];
            foreach (var cell in new[] { first, last }.Distinct())
            {
                FocusForSmoke(grid, grid, _pointsDialog, context + " point cell " + cell.OwningColumn?.Name);
                PerformLayoutRecursively(this);
                // Native fill layout estimates rows before reserving the horizontal
                // scrollbar and can backfill an extra row. Complete that layout before
                // CurrentCell uses ScrollIntoView with the final data viewport height.
                for (var pass = 0; pass < 2; pass++)
                {
                    grid.CurrentCell = null;
                    grid.CurrentCell = cell;
                    foreach (var (scroll, _) in viewports)
                    {
                        var target = grid.RectangleToScreen(grid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, false));
                        var viewport = scroll.RectangleToScreen(scroll.ClientRectangle);
                        var dx = target.Left < viewport.Left ? target.Left - viewport.Left : Math.Max(0, target.Right - viewport.Right);
                        var dy = target.Top < viewport.Top ? target.Top - viewport.Top : Math.Max(0, target.Bottom - viewport.Bottom);
                        if (dx == 0 && dy == 0) continue;
                        scroll.AutoScrollPosition = new Point(Math.Max(0, -scroll.AutoScrollPosition.X + dx),
                            Math.Max(0, -scroll.AutoScrollPosition.Y + dy));
                        PerformLayoutRecursively(this);
                    }
                }
                // The final ancestor adjustment may itself have invoked native fill
                // layout. Select once more without relayout, then check the real cell
                // against both its own viewport and every ancestor below.
                grid.CurrentCell = null;
                grid.CurrentCell = cell;
                AssertCellVisible(cell, requireFocus: true, "after real cell scrolling");
            }
        }
        catch
        {
            var content = Descendants(_pages["devices"]).OfType<TableLayoutPanel>()
                .FirstOrDefault(control => control.Name == "DevicesContent");
            var header = Descendants(_pages["devices"]).FirstOrDefault(control => control.Name == "TagActions");
            Console.WriteLine($"Point-grid layout diagnostic ({context}): client={ClientSize}, dpi={DeviceDpi}; "
                + $"DevicesContent={content?.Bounds}, rows=[{string.Join(",", content?.GetRowHeights() ?? [])}]; "
                + $"TagActions={header?.Bounds}; TagList={grid.Bounds}, visible={grid.Visible}, rowCount={grid.Rows.Count}; "
                + $"gridFirstRow={grid.FirstDisplayedScrollingRowIndex}, gridOffsets={grid.HorizontalScrollingOffset}/{grid.VerticalScrollingOffset}; "
                + string.Join("; ", viewports.Select(item => $"{item.Viewport.Name}: client={item.Viewport.ClientRectangle}, scroll={item.Viewport.AutoScrollPosition}")));
            throw;
        }
        finally
        {
            // Restore focus before the viewport, because native focus may itself scroll.
            if (priorFocus is { IsDisposed: false, Visible: true, Enabled: true }) priorFocus.Focus();
            state.Restore(grid, item => ((TagRow)item).Id);
            if (priorCell is null) grid.CurrentCell = null;
            grid.ClearSelection();
            foreach (var row in selectedRows)
                if (ReferenceEquals(row.GatewayTable, grid)) row.Selected = true;
            foreach (var (scroll, position) in viewports.AsEnumerable().Reverse())
                scroll.AutoScrollPosition = position;
            PerformLayoutRecursively(this);
            if (openedHere) _pointsDialog.Hide();
        }

        void AssertCellVisible(GatewayTableCell cell, bool requireFocus, string phase)
        {
            var bounds = grid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, false);
            var clipped = grid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true);
            if (!grid.Visible || cell.OwningRow?.DataBoundItem is not TagRow || bounds.Width <= 0 || bounds.Height <= 0
                || bounds != clipped || requireFocus && (!grid.ContainsFocus || grid.CurrentCell != cell))
                throw new InvalidOperationException($"{context}: point cell {cell.OwningColumn?.Name}, row {cell.RowIndex} "
                    + $"is not fully visible/focusable {phase}: bounds={bounds}, clipped={clipped}, focused={grid.ContainsFocus}.");
            var screenBounds = grid.RectangleToScreen(bounds);
            for (Control? parent = grid; parent is not null; parent = parent.Parent)
                if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(screenBounds))
                    throw new InvalidOperationException($"{context}: point cell {cell.OwningColumn?.Name}, row {cell.RowIndex} "
                        + $"is clipped by {parent.Name} {phase}: cell={screenBounds}, viewport={parent.RectangleToScreen(parent.ClientRectangle)}.");
        }
    }

    private static void BringIntoViewAndAssert(Control control, string context)
    {
        // A real operator selects the containing tab before focusing its input.
        // Select every ancestor tab from outermost to innermost; all existing focus
        // and ancestor clipping assertions still apply to the now visible field.
        var tabs = new List<(TabControl Owner, TabPage Page)>();
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent is TabPage page && page.Parent is TabControl owner) tabs.Add((owner, page));
        foreach (var (owner, page) in tabs.AsEnumerable().Reverse()) owner.SelectedTab = page;
        var form = control.FindForm() ?? throw new InvalidOperationException(context + ": control has no window.");
        // Tab selection changes effective visibility only after the WinForms layout pass.
        // Run it before focusing/asserting controls so the smoke test observes the same
        // settled layout that a real operator sees after switching tabs.
        PerformLayoutRecursively(form);
        Control? stableFocus = form switch
        {
            EditorDialog editor => editor.SmokeCommands.Last(),
            WriteValueDialog editor => editor.SmokeCommands.Last(),
            MainForm main => main.navigationMenu,
            _ => form
        };
        var focusTarget = control.Enabled && control.CanSelect ? control : stableFocus;
        FocusForSmoke(focusTarget, control, form, context);
        var surface = control.Parent as InputFrame;
        for (var parent = control.Parent; parent is not null && parent != form; parent = parent.Parent)
            if (parent is ScrollableControl scroll && scroll.AutoScroll) scroll.ScrollControlIntoView(surface ?? control);
        PerformLayoutRecursively(form);
        AssertVisibleBounds(control, context);
        AssertInputSurface(control, context);
        if (control is Button button) AssertButtonContentFits(button, context);
        foreach (var child in Descendants(control).Where(x => x.Visible && x is TextBox or System.Windows.Forms.TextBoxBase))
            AssertVisibleBounds(child, context + " input content");
    }

    private static void AssertInputSurface(Control control, string context)
    {
        if (control is NumericUpDown number && number.ThousandsSeparator)
            throw new InvalidOperationException(context + ": thousands separators enabled: " + control.Name);
        if (control is GatewaySwitch toggle)
        {
            AssertVisibleBounds(toggle.SwitchControl, context + " switch track");
            if (toggle.Width != UiTheme.Scale(toggle, 52) || toggle.Height < UiTheme.Scale(toggle, 30))
                throw new InvalidOperationException($"{context}: switch track has invalid bounds: {toggle.Name} {toggle.Bounds}.");
            if (toggle.Controls.OfType<Label>().Any(label => label.Visible))
                throw new InvalidOperationException(context + ": switch has a redundant caption: " + toggle.Name);
        }
        if (control is not (TextBox { Multiline: false } or ComboBox or NumericUpDown or DateTimePicker)) return;
        // The pagination size selector is a standalone official Select in a fixed-height footer,
        // rather than a labeled form field. Check its real bounds using that contract.
        if (control is ComboBox && control.Parent is TableLayoutPanel footer
            && footer.Controls.OfType<AntdUI.Pagination>().Any())
        {
            AssertVisibleBounds(control, context + " pagination size selector");
            if (control.Width > UiTheme.Scale(control, 136) + 1
                || Math.Abs(control.Height - UiTheme.Scale(control, UiTheme.ActionHeight)) > 1)
                throw new InvalidOperationException(context + ": pagination size selector has incorrect dimensions: " + control.Bounds);
            return;
        }
        if (control.Parent is not InputFrame surface)
            throw new InvalidOperationException(context + ": single-line editor is missing its button-height surface: " + control.Name);
        AssertVisibleBounds(surface, context + " input surface");
        if (surface.Width > UiTheme.Scale(surface, surface.LogicalWidthLimit) + 1
            || control.Width > surface.ClientSize.Width)
            throw new InvalidOperationException(context + ": editor exceeds its compact field width: " + control.Name);
        if (control is AntdUI.Input && (surface.Back != Color.Transparent || surface.BorderWidth != 0))
            throw new InvalidOperationException(context + ": input surface paints duplicate chrome: " + control.Name);
        var expected = Math.Max(UiTheme.Scale(control, UiTheme.ActionHeight),
            TextRenderer.MeasureText("确定", control.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height
            + UiTheme.Scale(control, 14));
        if (Math.Abs(surface.Height - expected) > 1 || Math.Abs(control.Top - (surface.ClientSize.Height - control.Height) / 2) > 1)
            throw new InvalidOperationException($"{context}: input surface height/centering differs from buttons: surface={surface.Height}, expected={expected}, editor={control.Bounds}.");
    }

    private static void FocusForSmoke(Control target, Control requested, Form window, string context)
    {
        if (target.Focus() && target.ContainsFocus) return;
        var initial = DescribeSmokeControl(target);
        var focusDiagnostics = (window as MainForm ?? window.Owner as MainForm)?._smokeFocusDiagnostics;
        focusDiagnostics?.Capture(context + " first Focus failure", target, requested, window, failure: true);
        // A modeless owned dialog was just closed, or a whole page was shown after
        // resizing. The CI message loop can still have window-activation/focus work
        // queued. Complete that lifecycle before judging keyboard reachability.
        // Never skip the focus or clipping assertions and never click a command.
        var timer = Stopwatch.StartNew();
        var attempts = 0;
        for (var attempt = 1; attempt <= 3 && timer.Elapsed < TimeSpan.FromMilliseconds(500); attempt++)
        {
            if (window.IsDisposed || target.IsDisposed || !window.Visible || !target.Visible || !target.Enabled) break;
            attempts = attempt;
            window.Activate();
            System.Windows.Forms.Application.DoEvents();
            if (window.IsDisposed || target.IsDisposed) break;
            PerformLayoutRecursively(window);
            if (target.Focus() && target.ContainsFocus)
            {
                var recovery = $"{context}: completed focus after {attempt} activation/message-loop pass(es); target={DescribeSmokeControl(target)}; requested={DescribeSmokeControl(requested)}; initial={initial}.";
                SmokeFocusRecoveries.Add(recovery);
                Console.WriteLine(recovery);
                focusDiagnostics?.Capture(context + " Focus recovered", target, requested, window,
                    attempts: attempts, elapsedMilliseconds: timer.Elapsed.TotalMilliseconds);
                return;
            }
        }
        focusDiagnostics?.Capture(context + " Focus attempts exhausted", target, requested, window, failure: true,
            attempts: attempts, elapsedMilliseconds: timer.Elapsed.TotalMilliseconds);
        throw new InvalidOperationException($"{context}: cannot focus target={DescribeSmokeControl(target)} before scrolling requested={DescribeSmokeControl(requested)}; initial={initial}; "
            + $"window={window.GetType().Name} '{window.Text}', active={ReferenceEquals(Form.ActiveForm, window)}, containsFocus={window.ContainsFocus}, activeControl={DescribeSmokeControl(window.ActiveControl)}.");
    }

    private static string DescribeSmokeControl(Control? control) => control is null ? "<none>"
        : $"{control.GetType().Name} name='{control.Name}' text='{control.Text}' bounds={control.Bounds} visible={control.Visible} enabled={control.Enabled} handleCreated={control.IsHandleCreated} canFocus={control.CanFocus} containsFocus={control.ContainsFocus}";

    private static void AssertButtonContentFits(Button button, string context)
    {
        var preferred = button.GetPreferredSize(Size.Empty);
        if (button.Width < preferred.Width || button.Height < preferred.Height)
            throw new InvalidOperationException($"{context}: button '{button.Text}' {button.Size} is smaller than its untruncated text/icon layout {preferred}.");
    }

    private static void ResetScrollPositions(Control root, Control stableFocus)
    {
        stableFocus.Focus();
        foreach (var scroll in Descendants(root).Prepend(root).OfType<ScrollableControl>().Where(x => x.AutoScroll))
            scroll.AutoScrollPosition = Point.Empty;
        PerformLayoutRecursively(root);
    }

    private string SmokeOutputDirectory(string suffix)
    {
        var path = string.IsNullOrWhiteSpace(_smokeScreenshotPath)
            ? Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-smoke.png")
            : Path.GetFullPath(_smokeScreenshotPath);
        return Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + suffix);
    }

    private void TrySaveSmokeFailure(Form window, string name)
    {
        TrySaveSmokeScreenshot(window, Path.Combine(SmokeOutputDirectory("failures"), name + ".png"));
    }

    private static void TrySaveSmokeScreenshot(Form window, string path)
    {
        try { SaveSmokeScreenshot(window, path); }
        catch (Exception exception) { Console.Error.WriteLine("Could not save failure screenshot: " + exception.Message); }
    }

    private static void SaveSmokeScreenshot(Form window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(Math.Max(1, window.Width), Math.Max(1, window.Height));
        DrawSmokeWindow(window, bitmap, Path.GetFileName(path));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void WriteLayoutReport(string directory, List<LayoutCaseResult> results, int actualDpi, uint nativeDpi)
    {
        var report = new
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            ViewportSimulation = true,
            NativeWindowResize = true,
            Explanation = "Nominal resolution/scaling pairs are mapped to equivalent available logical space at the host's unchanged actual DPI. No WM_DPICHANGED or DeviceDpi is fabricated. This is not real multi-DPI, multi-monitor, or multi-Windows-version certification.",
            TaskbarReserveLogicalPixels = 48,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            WinFormsVersion = typeof(Form).Assembly.GetName().Version?.ToString(),
            ActualDeviceDpi = actualDpi, NativeWindowDpi = nativeDpi,
            ActualMonitorBounds = Screen.FromControl(this).Bounds,
            ActualMonitorWorkingArea = Screen.FromControl(this).WorkingArea,
            ExpectedCases = 20, ExecutedCases = results.Count, PassedCases = results.Count(x => x.Passed),
            CollectionRunning = _application.IsRunning,
            FocusRecoveryCount = SmokeFocusRecoveries.Count,
            FocusRecoveries = SmokeFocusRecoveries,
            ScreenshotVisibilityRestoreCount = SmokeScreenshotRestores.Count,
            ScreenshotVisibilityRestores = SmokeScreenshotRestores,
            Cases = results
        };
        File.WriteAllText(Path.Combine(directory, "layout-matrix.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private sealed class LayoutCaseResult
    {
        public string Name { get; init; } = "";
        public bool ViewportSimulation { get; } = true;
        public Size PhysicalResolution { get; init; }
        public int NominalScalePercent { get; init; }
        public int NominalDpi { get; init; }
        public int ActualDeviceDpi { get; init; }
        public uint NativeWindowDpi { get; init; }
        public SizeF EquivalentLogicalWorkArea { get; init; }
        public Rectangle RequestedWindowBounds { get; init; }
        public Rectangle ActualWindowBounds { get; set; }
        public List<string> CheckedPages { get; } = [];
        public List<string> CheckedDialogs { get; } = [];
        public List<ControlMetric> HeaderMetrics { get; set; } = [];
        public List<ControlMetric> DeviceParameterMetrics { get; set; } = [];
        public List<ControlMetric> ConfigurationParameterMetrics { get; set; } = [];
        public List<ControlMetric> ChannelInputMetrics { get; set; } = [];
        public List<ControlMetric> DatabaseConnectionMetrics { get; set; } = [];
        public List<ControlMetric> LoggingInputMetrics { get; set; } = [];
        public int DatabaseMappingCellsChecked { get; set; }
        public bool DatabasePointSelectionVerified { get; set; }
        public List<ControlMetric> WriteVerificationMetrics { get; set; } = [];
        public ControlMetric? WriteInputMetric { get; set; }
        public bool Passed { get; set; }
        public string? Error { get; set; }
    }

    private sealed record ControlMetric(string Name, string Type, Size Size, Size MinimumSize,
        float FontPoints, int FontHeightPixels, int ActualDeviceDpi, Padding Padding, Padding Margin)
    {
        public static ControlMetric From(Control control) => new(control.Name, control.GetType().Name,
            control.Size, control.MinimumSize, control.Font.SizeInPoints, control.Font.Height,
            control.DeviceDpi, control.Padding, control.Margin);
    }
}
