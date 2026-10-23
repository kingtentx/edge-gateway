using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.OpcUa;
using EdgeGateway.Service;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm : DpiAwareForm
{
    private GatewayConfiguration _configuration;
    private MqttConfiguration _appliedMqtt;
    private readonly IConfigurationStore _configurationStore;
    private readonly IGatewayStore _store;
    private readonly IDriverRegistry _drivers;
    private readonly GatewayRuntime _runtime;
    private readonly GatewayOpcUaServer _opcUaServer;
    private readonly IGatewayLog _log;
    private readonly string _dataDirectory;
    private readonly bool _smokeTest;
    private readonly bool _layoutMatrix;
    private readonly string? _smokeScreenshotPath;
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly List<Control> _configurationCommands = [];
    private readonly List<Control> _stoppedConfigurationCommands = [];
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };
    private readonly Label _runState = UiTheme.Label("已停止", 10, UiTheme.Muted, true);
    private readonly Label _savedState = UiTheme.Label("配置已保存", 9, UiTheme.Muted);
    private readonly Label _footer = UiTheme.Label("就绪", 9, UiTheme.Muted);
    private readonly Label _deviceCount = UiTheme.Label("0 / 0", 26, UiTheme.Ink, true);
    private readonly Label _pointCount = UiTheme.Label("0", 26, UiTheme.Ink, true);
    private readonly Label _pendingCount = UiTheme.Label("0", 26, UiTheme.Ink, true);
    private readonly Label _mqttStatus = UiTheme.Label("未启用", 20, UiTheme.Muted, true);
    private readonly DataGridView _overviewGrid = UiTheme.Grid();
    private readonly Label _automaticSaveState = UiTheme.Label("配置自动保存", 9, UiTheme.Muted);
    private string _activePage = "overview";
    private bool _dirty, _busy, _refreshing, _closing, _allowClose;
    public bool SmokeTestFailed { get; private set; }

    public MainForm(GatewayConfiguration configuration, IConfigurationStore configurationStore,
        IGatewayStore store, IDriverRegistry drivers, GatewayRuntime runtime, GatewayOpcUaServer opcUaServer, IGatewayLog log,
        string dataDirectory, bool smokeTest = false, string? smokeScreenshotPath = null, bool layoutMatrix = false)
    {
        _configuration = Clone(configuration);
        _appliedMqtt = Clone(configuration.Mqtt);
        _configurationStore = configurationStore;
        _store = store;
        _drivers = drivers;
        _runtime = runtime;
        _opcUaServer = opcUaServer;
        _log = log;
        _dataDirectory = dataDirectory;
        _smokeTest = smokeTest || layoutMatrix;
        _layoutMatrix = layoutMatrix;
        _smokeScreenshotPath = smokeScreenshotPath;
        Text = "Edge Gateway · 边缘采集网关";
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
        var shell = new TableLayoutPanel { Name = "ApplicationShell", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 206));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var navigationViewport = new Panel { Name = "NavigationViewport", Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Navy, Margin = new Padding(0) };
        var sidebar = new TableLayoutPanel
        {
            Name = "NavigationContent", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiTheme.Navy, Padding = new Padding(12, 18, 10, 16), ColumnCount = 1, RowCount = 10, Margin = new Padding(0)
        };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var brand = new FlowLayoutPanel
        {
            Name = "NavigationBrand", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 20)
        };
        var brandTitle = UiTheme.Label("EDGE GATEWAY", 13, Color.White, true);
        var brandCaption = UiTheme.Label("工业数据 · 本地连接", 10, Color.FromArgb(160, 178, 203));
        brand.Controls.Add(brandTitle); brand.Controls.Add(brandCaption);
        sidebar.Controls.Add(brand, 0, 0);
        var entries = new[] { ("overview", "运行总览"), ("devices", "设备与点位"), ("realtime", "实时监控"), ("history", "历史与趋势"), ("diagnostics", "通信诊断"), ("cloud", "平台与系统"), ("opcua", "OPC UA服务"), ("logs", "运行日志") };
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var button = UiTheme.NavigationButton(entry.Item2, entry.Item1);
            button.Name = "Navigation_" + entry.Item1; button.Dock = DockStyle.Fill;
            button.AutoSize = true; button.MinimumSize = new Size(0, 48);
            button.Margin = new Padding(0, 0, 0, 6);
            button.Click += (_, _) => ShowPage(entry.Item1);
            _navigation[entry.Item1] = button;
            sidebar.Controls.Add(button, 0, i + 1);
        }
        sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var edition = UiTheme.Label("边缘数据工作台\n本地采集 / 可靠上报", 10, Color.FromArgb(142, 162, 190));
        edition.Name = "NavigationEdition"; edition.Dock = DockStyle.Fill; edition.Margin = new Padding(0, 20, 0, 0);
        sidebar.Controls.Add(edition, 0, entries.Length + 1);
        navigationViewport.Controls.Add(sidebar);

        var body = new TableLayoutPanel { Name = "ApplicationBody", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var header = new TableLayoutPanel
        {
            Name = "ApplicationHeader", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            BackColor = Color.White, Padding = new Padding(18, 10, 12, 10), AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var status = new FlowLayoutPanel
        {
            Name = "HeaderStatus", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0)
        };
        status.Controls.Add(_runState); status.Controls.Add(_savedState);
        // The header reports aggregate state; device commands live on their own rows.
        var headerInfo = new FlowLayoutPanel
        {
            Name = "HeaderSaveStatus", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, Dock = DockStyle.None,
            Margin = new Padding(12, 0, 0, 0), Padding = new Padding(0)
        };
        _automaticSaveState.Name = "AutomaticSaveState";
        headerInfo.Controls.Add(_automaticSaveState);
        header.Controls.Add(status, 0, 0); header.Controls.Add(headerInfo, 1, 0);
        body.Controls.Add(header, 0, 0); body.Controls.Add(_pageHost, 0, 1);
        _footer.Dock = DockStyle.Fill; _footer.Padding = new Padding(18, 0, 0, 0); _footer.AutoEllipsis = true;
        body.Controls.Add(_footer, 0, 2);
        shell.Controls.Add(navigationViewport, 0, 0); shell.Controls.Add(body, 1, 0);
        Controls.Add(shell);

        bool arranging = false;
        bool? compactHeader = null;
        void ArrangeShell()
        {
            if (arranging || !shell.IsHandleCreated || !IsDpiLayoutReady) return;
            arranging = true;
            try
            {
                var compactNavigation = shell.ClientSize.Width < UiTheme.Scale(shell, 1000);
                var sidebarWidth = UiTheme.Scale(shell, compactNavigation ? 172 : 206);
                if (shell.ColumnStyles[0].Width != sidebarWidth) shell.ColumnStyles[0].Width = sidebarWidth;
                brandTitle.Text = compactNavigation ? "EDGE" : "EDGE GATEWAY";
                brandCaption.Text = compactNavigation ? "采集网关" : "工业数据 · 本地连接";
                var sidebarTextWidth = Math.Max(1, sidebar.ClientSize.Width - sidebar.Padding.Horizontal);
                SetWrappingWidth(edition, sidebarTextWidth);
                var narrow = body.ClientSize.Width < UiTheme.Scale(body, 610);
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
                status.FlowDirection = narrow ? FlowDirection.LeftToRight : FlowDirection.TopDown;
                status.WrapContents = narrow;
                _runState.Margin = narrow ? UiTheme.Scale(status, new Padding(0, 4, 16, 4)) : UiTheme.Scale(status, new Padding(0, 4, 0, 4));
                headerInfo.Margin = narrow ? new Padding(0) : UiTheme.Scale(header, new Padding(12, 0, 0, 0));
                header.Controls.Add(status, 0, 0);
                header.Controls.Add(headerInfo, narrow ? 0 : 1, narrow ? 1 : 0);
                header.ResumeLayout(true);
            }
            finally { arranging = false; }
        }
        shell.SizeChanged += (_, _) => ArrangeShell();
        shell.HandleCreated += (_, _) => ArrangeShell();
        shell.DpiChangedAfterParent += (_, _) => { compactHeader = null; ArrangeShell(); };
        DpiLayoutCompleted += (_, _) => { compactHeader = null; ArrangeShell(); };
        body.SizeChanged += (_, _) => ArrangeShell();
        sidebar.SizeChanged += (_, _) => ArrangeShell();

        AddPage("overview", BuildOverviewPage());
        AddPage("devices", BuildDevicesPage());
        AddPage("realtime", BuildRealtimePage());
        AddPage("history", BuildHistoryPage());
        AddPage("diagnostics", BuildDiagnosticsPage());
        AddPage("cloud", BuildCloudPage());
        AddPage("opcua", BuildOpcUaPage());
        AddPage("logs", BuildLogsPage());
        ShowPage("overview");
    }

    private Control BuildOverviewPage()
    {
        UiTheme.AddColumn(_overviewGrid, "Name", "设备", 120, 140);
        UiTheme.AddColumn(_overviewGrid, "Driver", "驱动", 100);
        UiTheme.AddColumn(_overviewGrid, "State", "运行状态", 90);
        UiTheme.AddColumn(_overviewGrid, "Points", "启用点位", 75, 60);
        UiTheme.AddColumn(_overviewGrid, "LastSuccess", "最近成功", 150, 130);
        UiTheme.AddColumn(_overviewGrid, "Error", "连接信息", 180, 200);
        var content = new TableLayoutPanel { Name = "OverviewContent", ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var cards = new TableLayoutPanel
        {
            Name = "OverviewCards", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4, RowCount = 1, Margin = new Padding(0)
        };
        var cardInfo = new[] { ("在线 / 启用设备", _deviceCount, "独立连接，自动重连"), ("优质 / 最新采样", _pointCount, "质量与时间戳同步展示"), ("待补传消息", _pendingCount, "收到 MQTT 确认后移除"), ("平台连接", _mqttStatus, "QoS 1 · 持久化发送队列") };
        var cardPanels = new List<TableLayoutPanel>();
        for (var i = 0; i < 4; i++)
        {
            var accent = i switch { 0 => UiTheme.Blue, 1 => UiTheme.Green, 2 => Color.FromArgb(217, 119, 6), _ => Color.FromArgb(8, 145, 178) };
            var card = UiTheme.Card(accent);
            card.Name = "OverviewCard_" + i; card.Dock = DockStyle.Fill;
            card.AutoSize = true; card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Padding = new Padding(28, 16, 14, 14); card.ColumnCount = 1; card.RowCount = 3;
            card.Margin = new Padding(0, 0, 12, 12);
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 3; row++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var title = UiTheme.Label(cardInfo[i].Item1, 10, UiTheme.Muted);
            var detail = UiTheme.Label(cardInfo[i].Item3, 10, UiTheme.Muted);
            title.Dock = DockStyle.Fill; detail.Dock = DockStyle.Fill;
            card.Controls.Add(title, 0, 0); card.Controls.Add(cardInfo[i].Item2, 0, 1); card.Controls.Add(detail, 0, 2);
            card.SizeChanged += (_, _) =>
            {
                if (!IsDpiLayoutReady) return;
                var width = Math.Max(1, card.ClientSize.Width - card.Padding.Horizontal);
                SetWrappingWidth(title, width); SetWrappingWidth(detail, width);
            };
            cardPanels.Add(card); cards.Controls.Add(card, i, 0);
        }
        content.Controls.Add(cards, 0, 0);
        var deviceHeading = UiTheme.Label("设备连接状态", 12, UiTheme.Ink, true);
        deviceHeading.Margin = new Padding(0, 6, 0, 12);
        content.Controls.Add(deviceHeading, 0, 1); content.Controls.Add(_overviewGrid, 0, 2);
        var help = UiTheme.Label("首次使用：直接启动内置模拟设备，观察实时数据；添加真实设备后，按现场点表配置协议、地址和字节序。", 10, UiTheme.Muted);
        help.Dock = DockStyle.Fill; content.Controls.Add(help, 0, 3);
        var viewport = ScrollableContent(content, "OverviewViewport", () => cards.PreferredSize.Height + UiTheme.Scale(content, 260));
        bool arranging = false;
        void ArrangeCards()
        {
            if (arranging || !cards.IsHandleCreated || !IsDpiLayoutReady) return;
            arranging = true;
            try
            {
                var count = cards.ClientSize.Width >= UiTheme.Scale(cards, 920) ? 4 : cards.ClientSize.Width >= UiTheme.Scale(cards, 440) ? 2 : 1;
                if (cards.ColumnCount != count || cards.ColumnStyles.Count != count)
                {
                    cards.SuspendLayout();
                    cards.Controls.Clear(); cards.ColumnStyles.Clear(); cards.RowStyles.Clear();
                    cards.ColumnCount = count; cards.RowCount = 4 / count;
                    for (var column = 0; column < count; column++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / count));
                    for (var row = 0; row < cards.RowCount; row++) cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    for (var i = 0; i < cardPanels.Count; i++) cards.Controls.Add(cardPanels[i], i % count, i / count);
                    cards.ResumeLayout(true);
                }
                for (var i = 0; i < cardPanels.Count; i++)
                {
                    var margin = UiTheme.Scale(cards, new Padding(0, 0, i % count == count - 1 ? 0 : 12, 12));
                    if (cardPanels[i].Margin != margin) cardPanels[i].Margin = margin;
                    var textWidth = Math.Max(1, cardPanels[i].ClientSize.Width - cardPanels[i].Padding.Horizontal);
                    foreach (var label in cardPanels[i].Controls.OfType<Label>().Where(x => x.Font.SizeInPoints <= 12))
                        SetWrappingWidth(label, textWidth);
                }
                SetWrappingWidth(help, Math.Max(1, content.ClientSize.Width));
                viewport.PerformLayout();
            }
            finally { arranging = false; }
        }
        cards.SizeChanged += (_, _) => ArrangeCards();
        cards.HandleCreated += (_, _) => ArrangeCards();
        cards.DpiChangedAfterParent += (_, _) => ArrangeCards();
        DpiLayoutCompleted += (_, _) => ArrangeCards();
        content.SizeChanged += (_, _) => ArrangeCards();
        return UiTheme.PageLayout("运行总览", "从设备现场到数据平台，掌握每条采集链路的状态。", UiTheme.Toolbar(), viewport);
    }

    private void AddPage(string key, Control page)
    {
        page.Visible = false; page.Dock = DockStyle.Fill;
        _pages.Add(key, page); _pageHost.Controls.Add(page);
    }

    private void ShowPage(string key)
    {
        _activePage = key;
        foreach (var page in _pages) page.Value.Visible = page.Key == key;
        _pages[key].BringToFront();
        foreach (var nav in _navigation)
        {
            nav.Value.BackColor = nav.Key == key ? Color.FromArgb(39, 67, 106) : UiTheme.Navy;
            nav.Value.ForeColor = nav.Key == key ? Color.White : Color.FromArgb(190, 205, 225);
        }
        // Hidden grids may create their bound rows only when first displayed.
        // Refresh commands now rather than waiting for the next polling tick.
        UpdateCommands();
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        if (_smokeTest)
        {
            try
            {
                await RefreshSnapshotsAsync();
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
                    ResetScrollPositions(_pages[key], _navigation["overview"]);
                    using var bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
                    DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                    if (!string.IsNullOrWhiteSpace(_smokeScreenshotPath))
                    {
                        var requested = Path.GetFullPath(_smokeScreenshotPath);
                        var directory = Path.GetDirectoryName(requested)!;
                        Directory.CreateDirectory(directory);
                        var output = key == "overview" ? requested : Path.Combine(directory, Path.GetFileNameWithoutExtension(requested) + "-" + key + ".png");
                        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                SmokeDriverEditors();
                SmokeDynamicDriverSwitch();
                await SmokeDeviceConfigurationsAsync();
                await SmokeDeviceImportAsync();
                await SmokeDeviceActionsAndAutosaveAsync();
                await SmokeLiveConfigurationAsync();
                await SmokeCompletedCapabilitiesAsync();
                await SmokeOpcUaServiceAsync();
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
        if (_configuration.OpcUa.Enabled) await StartOpcUaServiceAsync(showError: false);
    }

    private static void PerformLayoutRecursively(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls)
            if (child.Visible) PerformLayoutRecursively(child);
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
                    if (parent is ScrollableControl scroll && scroll.AutoScroll) scroll.ScrollControlIntoView(input);
                PerformLayoutRecursively(editor);
                AssertVisibleBounds(input, context + " field");
                // A NumericUpDown is one operator field. Verify its internal edit box after
                // scrolling the whole control without treating it as another keyboard stop.
                foreach (var child in Descendants(input).Where(x => x.Visible && x is TextBoxBase))
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
            if (child is TextBoxBase or ComboBox or NumericUpDown or CheckBox) yield return child;
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
        using var bitmap = new Bitmap(editor.Width, editor.Height);
        editor.DrawToBitmap(bitmap, new Rectangle(0, 0, editor.Width, editor.Height));
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
        await _configurationStore.SaveAsync(_configuration);
        await _runtime.StartAsync();
        SetFooter("已按自动启动设置启动启用设备；可在设备行独立停止。");
    }

    private async Task ExecuteDeviceActionAsync(Guid deviceId, bool start)
    {
        if (_closing) return;
        var device = _configuration.Devices.FirstOrDefault(x => x.Id == deviceId)
            ?? throw new InvalidOperationException("设备不存在。");
        if (start)
        {
            if (!device.Enabled) throw new InvalidOperationException("设备未启用，请先在“配置 → 采集设置”勾选“启用设备”。");
            // Persist generated default identities before their first acquisition.
            // Starting another device must not reconfigure the active runtime.
            if (!_runtime.IsRunning) await _configurationStore.SaveAsync(_configuration);
            await _runtime.StartDeviceAsync(deviceId);
            SetFooter($"设备“{device.Name}”已启动，其他设备的采集状态保持不变。" + (_dirty ? "使用已保存的平台设置，平台草稿尚未应用。" : ""));
        }
        else
        {
            await _runtime.StopDeviceAsync(deviceId);
            SetFooter($"设备“{device.Name}”已停止，其他运行设备继续采集。");
        }
        UpdateCommands();
    }

    private async Task CommitConfigurationAsync(GatewayConfiguration candidate, Guid? selectDevice = null,
        string? successMessage = null, bool replaceCloudDraft = false)
    {
        if (_closing) throw new InvalidOperationException("程序正在退出，不能修改配置。");
        candidate = Clone(candidate);
        var opcUaChanged = JsonSerializer.Serialize(_configuration.OpcUa, JsonConfigurationStore.SerializerOptions)
            != JsonSerializer.Serialize(candidate.OpcUa, JsonConfigurationStore.SerializerOptions);
        var errors = ConfigurationValidator.Validate(candidate, _drivers);
        if (errors.Count > 0) throw new InvalidDataException("配置检查未通过：\n\n" + string.Join("\n", errors));
        if (_appliedMqtt.Platform == candidate.Mqtt.Platform && MqttDestinationChanged(_appliedMqtt, candidate.Mqtt))
        {
            var statistics = await _store.GetStatisticsAsync();
            if (statistics.OutboxCount > 0)
            {
                var response = MessageBox.Show(this,
                    $"当前仍有 {statistics.OutboxCount:N0} 条待发送消息。\n\n"
                    + $"原连接：{_appliedMqtt.Host}:{_appliedMqtt.Port}\n新连接：{candidate.Mqtt.Host}:{candidate.Mqtt.Port}\n\n"
                    + "应用后，这些消息会在平台启用时使用新的连接与凭据发送，消息中仍保留原网关标识和原主题。\n\n确认将待发送消息迁移到新连接？选择“否”可先使用原连接完成补传。",
                    "确认待发送消息的目标变更", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (response != DialogResult.Yes) throw new OperationCanceledException("已取消 MQTT 连接变更。");
            }
        }
        // Persistence and device-session changes share the runtime lifecycle lock.
        // A failed validation/write cannot stop an existing session or publish the draft.
        var applied = await _runtime.ApplyConfigurationAsync(candidate, _configurationStore);
        _configuration = candidate;
        _appliedMqtt = Clone(candidate.Mqtt);
        if (replaceCloudDraft) ResetCloudDraft();
        RebindConfigurationViews(selectDevice);
        var opcUaError = opcUaChanged ? await ReconfigureOpcUaAfterCommitAsync() : null;
        var outcome = applied.Restarted.Count > 0 ? $" 已对 {applied.Restarted.Count} 台变更设备发起重新连接，请查看采集状态。"
            : applied.Stopped.Count > 0 ? $" 已停止 {applied.Stopped.Count} 台被禁用或删除的设备。" : "";
        var message = (successMessage ?? "配置已自动保存，上一版备份已保留。") + outcome
            + (opcUaError is null ? "" : " OPC UA服务未启动：" + opcUaError);
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
            var samples = _runtime.GetLatest();
            var statuses = _runtime.GetDeviceStatuses();
            var enabled = _configuration.Devices.Count(x => x.Enabled);
            _deviceCount.Text = $"{statuses.Count(x => x.State == DeviceState.Running)} / {enabled}";
            _pointCount.Text = $"{samples.Count(x => x.Quality == SampleQuality.Good)} / {samples.Count}";
            _mqttStatus.Text = !_configuration.Mqtt.Enabled ? "未启用" : _runtime.IsMqttConnected ? "已连接" : _runtime.IsRunning ? "等待连接" : "已断开";
            _mqttStatus.ForeColor = _runtime.IsMqttConnected ? UiTheme.Green : UiTheme.Muted;
            if (_activePage == "overview")
            {
                var byId = statuses.ToDictionary(x => x.DeviceId);
                _overviewGrid.DataSource = _configuration.Devices.Select(d =>
                {
                    byId.TryGetValue(d.Id, out var s);
                    return new
                    {
                        d.Name, Driver = d.DriverId, State = !d.Enabled ? "未启用" : StateText(s?.State ?? DeviceState.Stopped),
                        Points = d.Tags.Count(t => t.Enabled), LastSuccess = s?.LastSuccess?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—",
                        Error = s?.Error ?? (d.DriverId == "simulator" ? "本地模拟设备" : "—")
                    };
                }).ToList();
            }
            if (_activePage == "realtime") RefreshRealtime(samples);
            if (_activePage == "logs") RefreshLogs();
            if (_activePage == "opcua") RefreshOpcUaStatus();
            var statistics = await _store.GetStatisticsAsync();
            if (!IsDisposed)
            {
                _pendingCount.Text = statistics.OutboxCount.ToString("N0", CultureInfo.CurrentCulture);
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
        var running = _runtime.IsRunning;
        foreach (var control in _configurationCommands) control.Enabled = !_busy && !_closing;
        foreach (var control in _stoppedConfigurationCommands) control.Enabled = !_busy && !_closing && !running;
        var activeDevices = _configuration.Devices.Count(x => _runtime.IsDeviceRunning(x.Id));
        _runState.Text = _busy ? "正在处理…" : running ? $"● {activeDevices} 台设备采集中" : "● 采集已停止";
        _runState.ForeColor = running ? UiTheme.Green : UiTheme.Muted;
        _savedState.Text = $"{_configuration.Name}  /  {_configuration.GatewayId}";
        _automaticSaveState.Text = _dirty && _opcUaDirty ? "两项设置待应用" : _opcUaDirty ? "OPC UA设置待应用" : _dirty ? "平台设置待应用" : "配置自动保存";
        _automaticSaveState.ForeColor = _dirty || _opcUaDirty ? Color.FromArgb(180, 83, 9) : UiTheme.Muted;
        UpdateDeviceConfigurationCommands();
        UpdateWriteButton();
        UpdateOpcUaCommands();
    }

    private void MarkDirty() { _dirty = true; UpdateCommands(); }
    private void SetFooter(string message) { if (!IsDisposed) _footer.Text = message; }
    private void ShowError(string message)
    {
        _log.Write(GatewayLogLevel.Error, message);
        SetFooter(message.Split('\n')[0]);
        if (!_smokeTest) MessageBox.Show(this, message, "采集网关", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        if (_busy) { SetFooter("正在处理当前操作，请稍后关闭窗口。"); return; }
        if (_dirty || _opcUaDirty)
        {
            var answer = MessageBox.Show(this, "平台或 OPC UA设置尚未应用，是否应用后退出？" + (_runtime.IsRunning ? "\n选择“是”将停止全部设备，然后应用并退出。" : ""), "尚未应用的设置", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes)
            {
                await RunBusyAsync(async () =>
                {
                    await _runtime.StopAsync();
                    await ApplyCloudSettingsAsync();
                    await ApplyOpcUaSettingsAsync();
                });
                // Failed/cancelled application keeps the draft and window open.
                if (_dirty || _opcUaDirty) return;
            }
        }
        _closing = true; _refreshTimer.Stop(); Enabled = false;
        try
        {
            await _opcUaServer.StopAsync();
            await _runtime.StopAsync();
            _allowClose = true; Close();
        }
        catch (Exception exception)
        {
            _closing = false; Enabled = true; _refreshTimer.Start(); ShowError("停止服务或采集失败：" + exception.Message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonConfigurationStore.SerializerOptions), JsonConfigurationStore.SerializerOptions)!;
    private static bool MqttDestinationChanged(MqttConfiguration previous, MqttConfiguration next) =>
        !string.Equals(previous.Host, next.Host, StringComparison.OrdinalIgnoreCase) || previous.Port != next.Port || previous.UseTls != next.UseTls
        || previous.Username != next.Username || previous.ClientId != next.ClientId || previous.PasswordEnvironmentVariable != next.PasswordEnvironmentVariable
        || previous.UsernameEnvironmentVariable != next.UsernameEnvironmentVariable || previous.PlatformGatewayId != next.PlatformGatewayId;
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
        System.Collections.IEnumerable structured => JsonSerializer.Serialize(structured, JsonConfigurationStore.SerializerOptions),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—"
    };
    private sealed record Choice(Guid? Id, string Name) { public override string ToString() => Name; }
}
