using EdgeGateway.Abstractions;
using EdgeGateway.Application;
using EdgeGateway.Model;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm : DpiAwareForm
{
    private GatewayConfiguration _configuration;
    private readonly IDriverCatalog _drivers;
    private readonly IGatewayApplication _application;
    private readonly IGatewayLog _log;
    private readonly string _driverDirectory;
    private readonly bool _smokeTest;
    private readonly bool _leaveWorkerRunning;
    private readonly NotifyIcon _trayIcon;
    private Form? _trayMenu;
    private readonly Icon _applicationIcon;
    private bool _exitRequested;
    private readonly bool _layoutMatrix;
    private readonly bool _databaseMappingSmoke;
    private readonly bool _databaseRefreshSmoke;
    private readonly bool _pageRefreshSmoke;
    private readonly bool _paginationSmoke;
    private readonly bool _historySourcesSmoke;
    private readonly bool _formWidthsSmoke;
    private readonly bool _buttonAppearanceSmoke;
    private readonly bool _navigationSmoke;
    private readonly bool _serverResourcesSmoke;
    private readonly bool _workbookSmoke;
    private readonly string? _smokeScreenshotPath;
    private Panel _pageHost = null!;
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<string, AntdUI.MenuItem> _navigation = [];
    private readonly List<Control> _configurationCommands = [];
    private readonly List<Control> _stoppedConfigurationCommands = [];
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };
    private Label _runState = null!;
    private Label _savedState = null!;
    private Label _footer = null!;
    private readonly AntdUI.TooltipComponent _feedbackTip = new() { Delay = 350 };
    private string _lastFeedback = "就绪";
    private Label _automaticSaveState = null!;
    private string _activePage = "overview";
    private bool _dirty, _busy, _refreshing, _closing, _allowClose;
    private readonly bool _loggingSmoke;
    public bool SmokeTestFailed { get; private set; }

    // Visual Studio must be able to construct the shell without a host or a tray icon.
    public MainForm()
    {
        _configuration = new(); _application = null!; _drivers = null!; _log = null!;
        _driverDirectory = ""; _trayIcon = null!; _trayMenu = null!; _applicationIcon = null!;
        Text = "Edge Gateway · 边缘采集网关";
        ConfigureWindow(new Size(1400, 860), new Size(960, 620));
        InitializeComponent();
    }

    public MainForm(IGatewayApplication application,
        bool smokeTest = false, string? smokeScreenshotPath = null, bool layoutMatrix = false,
        Func<IDisposable>? configurationWriteFailureForSmoke = null, string? driverDirectory = null, bool databaseMappingSmoke = false,
        bool serverResourcesSmoke = false, bool workbookSmoke = false, bool leaveWorkerRunning = false, bool loggingSmoke = false,
        bool databaseRefreshSmoke = false, bool pageRefreshSmoke = false, bool navigationSmoke = false, bool paginationSmoke = false, bool historySourcesSmoke = false, bool formWidthsSmoke = false, bool buttonAppearanceSmoke = false)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _configuration = application.GetConfiguration();
        _drivers = application.Drivers;
        _log = application.Log;
        _driverDirectory = Path.GetFullPath(driverDirectory ?? Path.Combine(AppContext.BaseDirectory, "drivers"));
        _loggingSmoke = loggingSmoke;
        _workbookSmoke = workbookSmoke;
        _leaveWorkerRunning = leaveWorkerRunning;
        _smokeTest = buttonAppearanceSmoke || formWidthsSmoke || historySourcesSmoke || paginationSmoke || navigationSmoke || pageRefreshSmoke || databaseRefreshSmoke || loggingSmoke || workbookSmoke || smokeTest || layoutMatrix || databaseMappingSmoke || serverResourcesSmoke;
        _paginationSmoke = paginationSmoke;
        _historySourcesSmoke = historySourcesSmoke;
        _formWidthsSmoke = formWidthsSmoke;
        _buttonAppearanceSmoke = buttonAppearanceSmoke;
        _navigationSmoke = navigationSmoke;
        _pageRefreshSmoke = pageRefreshSmoke;
        _databaseRefreshSmoke = databaseRefreshSmoke;
        _databaseMappingSmoke = databaseMappingSmoke;
        _serverResourcesSmoke = serverResourcesSmoke;
        _configurationWriteFailureForSmoke = configurationWriteFailureForSmoke;
        _layoutMatrix = layoutMatrix;
        _smokeScreenshotPath = smokeScreenshotPath;
        Text = "Edge Gateway · 边缘采集网关";
        _applicationIcon = TrayIconFactory.Create();
        Icon = _applicationIcon;
        _trayIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "Edge Gateway · 边缘采集网关",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            _trayMenu?.Close();
            _trayMenu = AntdUI.ContextMenuStrip.open(this, _trayIcon, item =>
            {
                if (item is not AntdUI.ContextMenuStripItem command) return;
                switch (command.ID)
                {
                    case "open": RestoreFromTray(); break;
                    case "status": ShowTrayStatus(); break;
                    case "exit": ExitFromTray(); break;
                }
            }, [new AntdUI.ContextMenuStripItem("打开 Edge Gateway") { ID = "open" },
                new AntdUI.ContextMenuStripItem("查看采集状态") { ID = "status" },
                new AntdUI.ContextMenuStripItemDivider(), new AntdUI.ContextMenuStripItem("退出网关") { ID = "exit" }]);
        };
        Font = new Font("Microsoft YaHei UI", 10F);
        ForeColor = UiTheme.Ink;
        BackColor = UiTheme.Background;
        ConfigureWindow(new Size(1400, 860), new Size(960, 620));
        StartPosition = FormStartPosition.CenterScreen;
        BuildShell();
        RebindConfigurationViews();
        UpdateCommands();
        _refreshTimer.Tick += async (_, _) => await RefreshSnapshotsAsync();
        Shown += OnShown;
        FormClosing += OnClosing;
    }

    private void BuildShell()
    {
        InitializeComponent();

        navigationApplicationIcon.Image = _applicationIcon.ToBitmap();
        _feedbackTip.SetTip(collapseNavigationButton, "收起导航菜单");
        var shell = applicationShell;
        var navigationViewport = navigationViewportPanel;
        var sidebar = navigationContentPanel;
        var body = applicationBody;
        var header = applicationHeader;
        var status = headerStatusPanel;
        var headerInfo = headerSaveStatusPanel;
        var brandTitle = brandTitleLabel;
        var brandCaption = brandCaptionLabel;

        _navigation.Add("overview", navigationOverviewItem);
        _navigation.Add("devices", navigationDevicesItem);
        _navigation.Add("realtime", navigationRealtimeItem);
        _navigation.Add("history", navigationHistoryItem);
        _navigation.Add("diagnostics", navigationDiagnosticsItem);
        _navigation.Add("database", navigationDatabaseItem);
        _navigation.Add("cloud", navigationCloudItem);
        _navigation.Add("logs", navigationLogsItem);
        navigationMenu.ItemClick += (_, e) => { if (!_selectingNavigation && e.Item.Name is { } key) ShowPage(key); };
        bool arranging = false;
        bool shellLayoutPending = false;
        bool? compactHeader = null;
        void ArrangeShell()
        {
            if (arranging || !shell.IsHandleCreated || !IsDpiLayoutReady) return;
            arranging = true;
            try
            {
                // Keep a stable logical rail width; AntdUI owns menu scrolling.
                var sidebarWidth = UiTheme.Scale(shell, navigationMenu.Collapsed ? 64 : 184);
                if (shell.ColumnStyles[0].Width != sidebarWidth)
                {
                    shell.ColumnStyles[0].Width = sidebarWidth;
                    // SizeChanged can arrive inside TableLayoutPanel's current pass.
                    // Finish the new column width on the next normal UI layout turn.
                    if (!shellLayoutPending)
                    {
                        shellLayoutPending = true;
                        shell.BeginInvoke((Action)(() =>
                        {
                            shellLayoutPending = false;
                            if (!shell.IsDisposed) shell.PerformLayout();
                        }));
                    }
                }
                brandTitle.Text = "EDGE";
                brandCaption.Text = "边缘采集网关";
                var narrow = body.ClientSize.Width < UiTheme.Scale(body, 400);
                if (compactHeader == narrow) return;
                compactHeader = narrow;
                header.SuspendLayout();
                header.Controls.Remove(status); header.Controls.Remove(headerInfo);
                header.ColumnStyles.Clear(); header.RowStyles.Clear();
                header.ColumnCount = narrow ? 1 : 2; header.RowCount = narrow ? 2 : 1;
                header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                if (!narrow) header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                if (narrow) header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                status.FlowDirection = FlowDirection.LeftToRight;
                status.WrapContents = true;
                _runState.Margin = UiTheme.Scale(status, new Padding(0, 4, 16, 4));
                headerInfo.Margin = narrow ? new Padding(0) : UiTheme.Scale(header, new Padding(12, 0, 0, 0));
                header.Controls.Add(status, 0, 0);
                header.Controls.Add(headerInfo, narrow ? 0 : 1, narrow ? 1 : 0);
                header.ResumeLayout(true);
            }
            finally { arranging = false; }
        }
        collapseNavigationButton.Click += (_, _) =>
        {
            // Hiding the AutoSize brand can synchronously enter the shell's layout.
            // Change menu state and column width together, then lay out once; updating
            // the column from that nested layout could leave its cached actual width stale.
            shell.SuspendLayout();
            sidebar.SuspendLayout();
            try
            {
                var collapsed = !navigationMenu.Collapsed;
                navigationMenu.SetRailCollapsed(collapsed);
                collapseNavigationButton.IconSvg = collapsed ? "MenuUnfoldOutlined" : "MenuFoldOutlined";
                brandTitleLabel.Visible = brandCaptionLabel.Visible = !collapsed;
                navigationBrandPanel.Visible = !collapsed;
                _feedbackTip.SetTip(collapseNavigationButton, collapsed ? "展开导航菜单" : "收起导航菜单");
                collapseNavigationButton.AccessibleName = collapsed ? "展开导航菜单" : "收起导航菜单";
                ArrangeShell();
            }
            finally
            {
                sidebar.ResumeLayout(true);
                shell.ResumeLayout(true);
            }
        };
        shell.SizeChanged += (_, _) => ArrangeShell();
        shell.HandleCreated += (_, _) => ArrangeShell();
        shell.DpiChangedAfterParent += (_, _) => { compactHeader = null; ArrangeShell(); };
        DpiLayoutCompleted += (_, _) => { compactHeader = null; ArrangeShell(); ArrangeDeviceColumns(); };
        body.SizeChanged += (_, _) => ArrangeShell();
        sidebar.SizeChanged += (_, _) => ArrangeShell();

        AddPage("overview", BuildOverviewPage());
        AddPage("devices", BuildDevicesPage());
        AddPage("realtime", BuildRealtimePage());
        AddPage("history", BuildHistoryPage());
        AddPage("diagnostics", BuildDiagnosticsPage());
        AddPage("database", BuildDatabasesPage());
        AddPage("cloud", BuildCloudPage());
        AddPage("logs", BuildLogsPage());
        ShowPage("overview");
    }

    private void AddPage(string key, Control page)
    {
        page.Visible = false; page.Dock = DockStyle.Fill;
        _pages.Add(key, page); _pageHost.Controls.Add(page);
    }

    private bool _selectingNavigation;

    private void ShowPage(string key)
    {
        _activePage = key;
        foreach (var page in _pages) page.Value.Visible = page.Key == key;
        _pages[key].BringToFront();
        _selectingNavigation = true;
        try { navigationMenu.Select(_navigation[key], false); }
        finally { _selectingNavigation = false; }
        // Hidden grids may create their bound rows only when first displayed.
        // Refresh commands now rather than waiting for the next polling tick.
        UpdateCommands();
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        if (_buttonAppearanceSmoke)
        {
            try { await SmokeButtonAppearanceAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true; TrySaveSmokeFailure(this, "button-appearance-failed"); Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_formWidthsSmoke)
        {
            try { await SmokeFormWidthsAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true; TrySaveSmokeFailure(this, "form-widths-failed"); Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_historySourcesSmoke)
        {
            try { await SmokeHistorySourcesAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true; TrySaveSmokeFailure(this, "history-sources-failed"); Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_paginationSmoke)
        {
            try { await SmokePaginationAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true; TrySaveSmokeFailure(this, "pagination-failed"); Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_navigationSmoke)
        {
            try { await SmokeNavigationCollapseAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true; TrySaveSmokeFailure(this, "navigation-collapse-failed"); Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_pageRefreshSmoke)
        {
            try { await SmokePageRefreshAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true; TrySaveSmokeFailure(this, "page-refresh-failed"); Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_databaseRefreshSmoke)
        {
            try { await SmokeDatabaseRefreshAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true; TrySaveSmokeFailure(this, "database-refresh-failed"); Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_loggingSmoke)
        {
            try { await SmokeLoggingAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true; TrySaveSmokeFailure(this, "logging-failed"); Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_workbookSmoke)
        {
            try { await SmokeWorkbooksAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true;
                TrySaveSmokeFailure(this, "workbooks-failed");
                Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_serverResourcesSmoke)
        {
            try { await SmokeServerResourceChartsAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true;
                TrySaveSmokeFailure(this, "server-resources-failed");
                Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_databaseMappingSmoke)
        {
            try { await SmokeCustomPointMappingAsync(); }
            catch (Exception exception)
            {
                SmokeTestFailed = true;
                TrySaveSmokeFailure(this, "database-mapping-failed");
                Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close(); return;
        }
        if (_smokeTest)
        {
            try
            {
                await RefreshSnapshotsAsync();
                await SmokeInitialOverviewScrollAsync();
                await SmokeOverviewAsync();
                SmokeDeferredPageNavigation();
                await SmokeAntdNavigationAndSelectAsync();
                await SmokeDefaultButtonStatesAsync();
                var originalSize = Size;
                Width = MinimumSize.Width;
                PerformLayoutRecursively(this);
                AssertHeaderStatusFits();
                Size = originalSize;
                foreach (var key in _pages.Keys)
                {
                    ShowPage(key);
                    await RefreshSnapshotsAsync();
                    PerformLayoutRecursively(this);
                    AssertHeaderStatusFits();
                    AssertPageToolbarsFit(_pages[key], key);
                    ResetScrollPositions(_pages[key], navigationMenu);
                    if (key == "devices")
                    {
                        VerifyPointRowsForSmoke("baseline devices", verifyInitialViewport: true);
                        SmokeListSpace();
                    }
                    using var bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
                    DrawSmokeWindow(this, bitmap, "baseline " + key);
                    if (!string.IsNullOrWhiteSpace(_smokeScreenshotPath))
                    {
                        var requested = Path.GetFullPath(_smokeScreenshotPath);
                        var directory = Path.GetDirectoryName(requested)!;
                        Directory.CreateDirectory(directory);
                        var output = key == "overview" ? requested : Path.Combine(directory, Path.GetFileNameWithoutExtension(requested) + "-" + key + ".png");
                        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                await SmokeConfigurationCatalogsAsync();
                SmokeDriverEditors();
                SmokeDynamicDriverSwitch();
                await SmokeDeviceConfigurationsAsync();
                await SmokeDeviceImportAsync();
                await SmokeDeviceActionsAndAutosaveAsync();
                await SmokeLiveConfigurationAsync();
                await SmokeCompletedCapabilitiesAsync();
                await SmokeUsabilityAsync();
                await SmokeDataRecordingAsync();
                if (_layoutMatrix) await RunLayoutMatrixAsync();
            }
            catch (Exception exception)
            {
                SmokeTestFailed = true;
                TrySaveSmokeFailure(this, "baseline-or-matrix-failed");
                Console.Error.WriteLine(exception);
            }
            _allowClose = true; Close();
            return;
        }
        _refreshTimer.Start();
        await RefreshSnapshotsAsync();
        if (_configuration.AutoStart) await RunBusyAsync(StartCollectionAsync);
    }

    private static void PerformLayoutRecursively(Control root)
    {
        // AntdUI queues tab-page activation through BeginInvoke. Complete that UI
        // turn before checking the selected page's actual bounds.
        if (root is Form { Visible: true }) System.Windows.Forms.Application.DoEvents();
        root.PerformLayout();
        foreach (Control child in root.Controls)
            if (child.Visible && child is not AntdUI.TabPage { Showed: false }) PerformLayoutRecursively(child);
        root.PerformLayout();
    }

    private void SmokeDriverEditors()
    {
        foreach (var driver in _drivers.Drivers)
        {
            var device = CreateConfigurationSmokeDevice(driver);
            var before = ConfigurationSmokeJson(device);
            using (var editor = new DeviceConfigurationDialog(device, _drivers, isNew: true))
            {
                editor.Show(this);
                try
                {
                    editor.VerifyBindingsForSmoke();
                    VerifyEditorLayout(editor, driver.Id + " device");
                    SaveEditorScreenshot(editor, "device", driver.Id);
                    var added = editor.CommitForSmoke();
                    if (added.DriverId != driver.Id || added.Id != device.Id || added.Tags.Count != 1
                        || device.Tags.Any(original => added.Tags[0].Id == original.Id))
                        throw new InvalidOperationException(driver.Id + ": unified add dialog lost the selected driver or reused an existing point.");
                    foreach (var parameter in driver.Parameters.Where(x => x.BoundProperty is null))
                        if (!added.Properties.ContainsKey(parameter.Key))
                            throw new InvalidOperationException(driver.Id + ": unified add dialog lost parameter " + parameter.Key);
                    AssertConfigurationSmokeUnchanged(before, device, driver.Id + " unified add source");
                }
                catch { SaveEditorScreenshot(editor, "device-failed", driver.Id); throw; }
                editor.Close();
            }
            using (var editor = new TagEditor(TagEditor.CreateDefaultTag(driver, "点位配置检查"), driver))
            {
                editor.Show(this);
                try
                {
                    editor.VerifyBindingsForSmoke();
                    VerifyEditorLayout(editor, driver.Id + " tag");
                    SaveEditorScreenshot(editor, "tag", driver.Id);
                }
                catch { SaveEditorScreenshot(editor, "tag-failed", driver.Id); throw; }
                editor.Close();
            }
        }
        Console.WriteLine($"WinForms editor smoke passed: {_drivers.Drivers.Count} drivers, {_drivers.Drivers.Sum(x => x.Parameters.Count)} reflected parameters, {_drivers.Drivers.Sum(x => x.Methods.Count)} methods.");
    }

    private static void VerifyEditorLayout(EditorDialog editor, string context)
    {
        var selected = editor.SelectedSmokeTab;
        foreach (var tab in editor.SmokeTabs)
        {
            editor.SelectedSmokeTab = tab;
            PerformLayoutRecursively(editor);
            foreach (var command in editor.SmokeCommands) AssertVisibleBounds(command, context + " action");
            // Long protocol settings pages must allow every field to be brought into view.
            foreach (var input in EditorInputs(tab))
            {
                if (!input.Visible) continue;
                // Follow the same focus path as keyboard navigation. ContainerControl may
                // scroll its ActiveControl during layout; leaving focus on the first field
                // would undo ScrollControlIntoView when testing a later field.
                // Disabled fields are inspected with focus on the stable footer Cancel
                // button (focus only, no click), clearing the previous input's focus chain.
                var focusTarget = input.Enabled ? input : editor.SmokeCommands.Last();
                if (!focusTarget.Focus() || !focusTarget.ContainsFocus)
                    throw new InvalidOperationException($"{context}: could not focus {focusTarget.GetType().Name} '{focusTarget.Name}' / '{focusTarget.Text}' before inspecting field '{input.Name}'.");
                for (var parent = input.Parent; parent is not null && parent != editor; parent = parent.Parent)
                    if (parent is ScrollableControl scroll && scroll.AutoScroll) scroll.ScrollControlIntoView(input.Parent as InputFrame ?? input);
                PerformLayoutRecursively(editor);
                AssertVisibleBounds(input, context + " field");
                AssertInputSurface(input, context);
                // A NumericUpDown is one operator field. Verify its internal edit box after
                // scrolling the whole control without treating it as another keyboard stop.
                foreach (var child in Descendants(input).Where(x => x.Visible && x is TextBox or System.Windows.Forms.TextBoxBase))
                    AssertVisibleBounds(child, context + " field content");
            }
            EditorInputs(tab).FirstOrDefault(x => x.Visible && x.Enabled)?.Focus();
            foreach (var scroll in Descendants(tab).OfType<ScrollableControl>().Where(x => x.AutoScroll)) scroll.AutoScrollPosition = Point.Empty;
            tab.AutoScrollPosition = Point.Empty;
        }
        editor.SelectedSmokeTab = selected;
        PerformLayoutRecursively(editor);
        editor.VerifyHeadingForSmoke(context);
    }

    private static IEnumerable<Control> EditorInputs(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is TextBox or System.Windows.Forms.TextBoxBase or ComboBox or NumericUpDown or CheckBox) yield return child;
            else foreach (var input in EditorInputs(child)) yield return input;
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private void SaveEditorScreenshot(Form editor, string kind, string driverId)
    {
        if (string.IsNullOrWhiteSpace(_smokeScreenshotPath)) return;
        var requested = Path.GetFullPath(_smokeScreenshotPath);
        var identifier = new string(driverId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        var output = Path.Combine(Path.GetDirectoryName(requested)!, Path.GetFileNameWithoutExtension(requested) + "-" + kind + "-" + identifier + ".png");
        if (editor is AntdUI.ILayeredForm layered)
        {
            // Layered popups render through UpdateLayeredWindow, outside DrawToBitmap.
            using var rendered = layered.PrintBit() ?? throw new InvalidOperationException("AntdUI popup produced no rendered image.");
            rendered.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        using var bitmap = new Bitmap(editor.Width, editor.Height);
        DrawSmokeWindow(editor, bitmap, Path.GetFileName(output));
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void AssertHeaderStatusFits()
    {
        foreach (var label in new[] { _runState, _savedState, _automaticSaveState })
            AssertVisibleBounds(label, "header state");
        if (Descendants(this).OfType<Button>().Any(x => x.Name is "StartCollection" or "StopCollection" or "SaveConfiguration"))
            throw new InvalidOperationException("Removed global acquisition/save commands are still present.");
    }

    private static void AssertPageToolbarsFit(Control root, string page)
    {
        if (root is AntdUI.TabPage { Showed: false }) return;
        if (root is TextBox or System.Windows.Forms.TextBoxBase or ComboBox or NumericUpDown or DateTimePicker or CheckBox)
        {
            BringIntoViewAndAssert(root, page + " input");
            if (root is GatewaySwitch) AssertInputSurface(root, page);
            return;
        }
        if (root is FlowLayoutPanel toolbar)
        {
            foreach (Control child in toolbar.Controls)
                if (child.Visible) BringIntoViewAndAssert(child, page + " toolbar");
        }
        foreach (Control child in root.Controls)
            if (child.Visible) AssertPageToolbarsFit(child, page);
    }

    private static void AssertVisibleBounds(Control control, string context)
    {
        if (!control.Visible || control.Width <= 0 || control.Height <= 0)
            throw new InvalidOperationException($"{context}: {control.GetType().Name} '{control.Name}' / '{control.Text}' has no visible area.");
        var bounds = control.Parent!.RectangleToScreen(control.Bounds);
        for (var ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            var viewport = ancestor.RectangleToScreen(ancestor.ClientRectangle);
            if (!viewport.Contains(bounds))
                throw new InvalidOperationException($"{context}: {control.GetType().Name} '{control.Name}' / '{control.Text}' ({bounds}) is clipped by {ancestor.GetType().Name} ({viewport}).");
        }
    }

    private async Task StartCollectionAsync()
    {
        // AutoStart remains an explicit saved policy. There is no global start button.
        await _application.StartAsync();
        SetFooter("已按自动启动设置启动启用设备；可在设备行独立停止。");
    }

    private async Task ExecuteDeviceActionAsync(Guid deviceId, bool start)
    {
        if (_closing) return;
        var device = _configuration.Devices.FirstOrDefault(x => x.Id == deviceId)
            ?? throw new InvalidOperationException("设备不存在。");
        if (start)
        {
            if (!device.Enabled) throw new InvalidOperationException("设备未启用，请先在“配置 → 采集设置”打开开关“启用设备”。");
            // Persist generated default identities before their first acquisition.
            // Starting another device must not reconfigure the active runtime.
            await _application.StartDeviceAsync(deviceId);
            SetFooter($"设备“{device.Name}”已启动，其他设备的采集状态保持不变。" + (_dirty ? "使用已保存的平台设置，平台草稿尚未应用。" : ""));
        }
        else
        {
            await _application.StopDeviceAsync(deviceId);
            SetFooter($"设备“{device.Name}”已停止，其他运行设备继续采集。");
        }
        UpdateCommands();
    }

    private async Task CommitConfigurationAsync(GatewayConfiguration candidate, Guid? selectDevice = null,
        string? successMessage = null, bool replaceCloudDraft = false, bool replaceConfiguration = false)
    {
        if (_closing) throw new InvalidOperationException("程序正在退出，不能修改配置。");
        var options = await ConfirmConfigurationChangeAsync(candidate);
        var applied = replaceConfiguration
            ? await _application.ReplaceConfigurationAsync(candidate, options)
            : await _application.ApplyConfigurationAsync(candidate, options);
        CompleteConfigurationCommit(applied, selectDevice, successMessage, replaceCloudDraft);
    }

    private async Task<ConfigurationApplyOptions?> ConfirmConfigurationChangeAsync(GatewayConfiguration candidate)
    {
        var preview = await _application.PreviewConfigurationAsync(candidate);
        ConfigurationApplyOptions? options = null;
        if (preview.RequiresMqttDestinationConfirmation)
        {
            var response = MessageBox.Show(this,
                $"当前仍有 {preview.PendingOutboxMessages:F0} 条待发送消息。\n\n"
                + $"原连接：{preview.PreviousMqtt.Host}:{preview.PreviousMqtt.Port}\n新连接：{preview.ProposedMqtt.Host}:{preview.ProposedMqtt.Port}\n\n"
                + "应用后，待发送消息将使用新的连接，仍保留原网关标识和主题。\n确认将这些消息迁移到新连接？",
                "确认待发送消息的目标变更", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (response != DialogResult.Yes) throw new OperationCanceledException("已取消 MQTT 连接变更。");
            options = new ConfigurationApplyOptions { MqttDestinationConfirmation = preview.ConfirmationToken };
        }
        return options;
    }

    private void CompleteConfigurationCommit(ConfigurationApplyResult applied, Guid? selectDevice = null,
        string? successMessage = null, bool replaceCloudDraft = false, Guid? selectTag = null)
    {
        _configuration = _application.GetConfiguration();
        if (replaceCloudDraft) ResetCloudDraft();
        RebindConfigurationViews(selectDevice, selectTag);
        var outcome = applied.Restarted.Count > 0 ? $" 已对 {applied.Restarted.Count} 台变更设备发起重新连接，请查看采集状态。"
            : applied.Stopped.Count > 0 ? $" 已停止 {applied.Stopped.Count} 台被禁用或删除的设备。" : "";
        var message = (successMessage ?? "配置已自动保存并应用。") + outcome;
        SetFooter(message);
        _log.Write(GatewayLogLevel.Information, message);
        UpdateCommands();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy || _closing) return;
        _busy = true; UpdateCommands(); UseWaitCursor = true;
        try { await action(); }
        catch (OperationCanceledException) { if (_smokeTest) throw; SetFooter("操作已取消。"); }
        catch (Exception exception) { if (_smokeTest) throw; ShowError(exception.Message); }
        finally { _busy = false; UseWaitCursor = false; UpdateCommands(); }
    }

    private async Task RefreshSnapshotsAsync()
    {
        if (_refreshing || _closing || IsDisposed) return;
        _refreshing = true;
        try
        {
            if (_activePage == "overview") RefreshOverview(_application.GetOverviewSummary());
            if (_activePage == "realtime") RefreshRealtime(_application.GetLatest());
            if (_activePage == "database")
            {
                var spool = await _application.GetAcquisitionSpoolStatisticsAsync();
                BindDatabaseViews(spool: spool);
            }
            var statistics = await _application.GetStatisticsAsync();
            if (!IsDisposed)
            {
                _pendingCount.Text = $"待补传 {statistics.OutboxCount:F0} 条";
                if (_activePage == "overview" && (_serverResourceRefresh is null || _serverResourceRefresh.IsCompleted))
                    _serverResourceRefresh = RefreshServerResourcesAsync();
                if (_activePage == "diagnostics") RefreshTelemetryDiagnostics(statistics);
            }
            UpdateCommands();
        }
        catch (Exception exception)
        {
            if (_smokeTest) throw;
            SetFooter("状态刷新失败：" + exception.Message);
        }
        finally { _refreshing = false; }
    }

    private void UpdateCommands()
    {
        var running = _application.IsRunning;
        foreach (var control in _configurationCommands) control.Enabled = !_busy && !_closing;
        foreach (var control in _stoppedConfigurationCommands) control.Enabled = !_busy && !_closing && !running;
        var activeDevices = _configuration.Devices.Count(x => _application.IsDeviceRunning(x.Id));
        _runState.Text = _busy ? "正在处理…" : running ? $"● {activeDevices} 台设备采集中" : "● 采集已停止";
        _runState.ForeColor = running ? UiTheme.Green : UiTheme.Muted;
        _savedState.Text = ClientSize.Height < UiTheme.Scale(this, 560) ? _configuration.Name : $"{_configuration.Name}  /  {_configuration.GatewayId}";
        _automaticSaveState.Text = _dirty || _loggingDirty ? "设置待应用" : "配置自动保存";
        _automaticSaveState.ForeColor = _dirty || _loggingDirty ? Color.FromArgb(180, 83, 9) : UiTheme.Muted;
        UpdateDeviceConfigurationCommands();
        UpdateSelectionCommands();
        UpdatePlatformCommands();
        UpdateLoggingCommands();
        UpdateHistoryQueryState();
        UpdateWriteButton();
    }

    private void MarkDirty() { _dirty = true; UpdateCommands(); }
    private void SetFooter(string message)
    {
        if (IsDisposed) return;
        _lastFeedback = message;
        if (_pointsDialog is { Visible: true }) _pointsDialog.SetFeedback(message);
        _footer.Text = DateTime.Now.ToString("HH:mm:ss") + "  ·  " + message.Replace('\n', ' ');
        _feedbackTip.SetTip(_footer, message + "\n点击查看完整说明");
        _footer.ForeColor = message.Contains("失败", StringComparison.Ordinal) ? UiTheme.Red : UiTheme.Muted;
    }
    private void ShowError(string message)
    {
        _log.Write(GatewayLogLevel.Error, message);
        SetFooter(message.Split('\n')[0]);
        if (!_smokeTest) MessageBox.Show(_pointsDialog is { Visible: true } ? _pointsDialog : this, message, "采集网关", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (!_smokeTest && !_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            HideToTray();
            return;
        }
        if (_closing) return;
        if (_busy) { _exitRequested = false; SetFooter("正在处理当前操作，请稍后关闭窗口。"); return; }
        if (_loggingDirty)
        {
            var answer = MessageBox.Show(this, "日志设置尚未应用，是否应用后退出？", "尚未应用的日志设置", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) { _exitRequested = false; return; }
            if (answer == DialogResult.Yes) { await RunBusyAsync(ApplyLoggingSettingsAsync); if (_loggingDirty) return; }
        }
        if (_dirty)
        {
            var answer = MessageBox.Show(this, "平台设置尚未应用，是否应用后退出？" + (_application.IsRunning ? "\n选择“是”将停止全部设备，然后应用并退出。" : ""), "尚未应用的设置", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) { _exitRequested = false; return; }
            if (answer == DialogResult.Yes)
            {
                await RunBusyAsync(async () =>
                {
                    var resumeCollection = _application.IsRunning;
                    await _application.StopAsync();
                    await ApplyCloudSettingsAsync();
                    if (resumeCollection) await _application.StartAsync();
                });
                // Failed/cancelled application keeps the draft and window open.
                if (_dirty) { _exitRequested = false; return; }
            }
        }
        _closing = true; _refreshTimer.Stop(); Enabled = false;
        try
        {
            if (_leaveWorkerRunning)
                await GatewayWorkerClient.RequestWorkerShutdownAsync();
            else
                await _application.StopAsync();
            _allowClose = true;
            if (_trayIcon is not null) _trayIcon.Visible = false;
            Close();
        }
        catch (Exception exception)
        {
            _exitRequested = false;
            _closing = false; Enabled = true; _refreshTimer.Start(); ShowError("停止采集失败：" + exception.Message);
        }
    }

    private void HideToTray()
    {
        if (!Visible) return;
        Hide();
        ShowInTaskbar = false;
        _trayIcon.Visible = true;
        _trayIcon.BalloonTipTitle = "Edge Gateway 仍在运行";
        _trayIcon.BalloonTipText = _application.IsRunning
            ? "窗口已隐藏，设备采集继续运行。右键托盘图标可查看状态或退出。"
            : "窗口已隐藏，网关程序仍在运行。右键托盘图标可打开窗口或退出。";
        _trayIcon.ShowBalloonTip(1800);
    }

    private void RestoreFromTray()
    {
        if (_allowClose || IsDisposed) return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ShowTrayStatus()
    {
        var state = _application.IsRunning ? "正在采集" : "采集已停止";
        var message = $"Edge Gateway：{state}{Environment.NewLine}窗口隐藏不会退出程序。";
        if (!IsDisposed) MessageBox.Show(this, message, "网关运行状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExitFromTray()
    {
        if (_exitRequested || _allowClose) return;
        if (_application.IsRunning)
        {
            var answer = MessageBox.Show(this,
                "退出网关将停止所有设备采集并关闭驱动连接。确定退出吗？",
                "退出 Edge Gateway", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        _exitRequested = true;
        RestoreFromTray();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _feedbackTip.Dispose();
            if (_trayIcon is not null) _trayIcon.Visible = false;
            _trayIcon?.Dispose();
            _trayMenu?.Dispose();
            navigationApplicationIcon?.Image?.Dispose();
            _applicationIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static T Clone<T>(T value) => GatewayJson.Clone(value);
    private static string StateText(DeviceState state) => state switch
    {
        DeviceState.Running => "运行中", DeviceState.Connecting => "连接中", DeviceState.Reconnecting => "重连中", DeviceState.Faulted => "异常", _ => "已停止"
    };
    private static string QualityText(SampleQuality quality) => quality switch { SampleQuality.Good => "正常", SampleQuality.Bad => "异常", _ => "不确定" };
    private static string ValueText(object? value) => value switch
    {
        null => "—", bool boolean => boolean ? "true" : "false", double number => number.ToString("G9", CultureInfo.InvariantCulture),
        float number => number.ToString("G7", CultureInfo.InvariantCulture),
        DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText(),
        string text => text,
        System.Collections.IEnumerable structured => JsonSerializer.Serialize(structured, GatewayJson.Options),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—"
    };
    private sealed record Choice(Guid? Id, string Name) { public override string ToString() => Name; }
}
