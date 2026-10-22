using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm : Form
{
    private GatewayConfiguration _configuration;
    private MqttConfiguration _appliedMqtt;
    private readonly IConfigurationStore _configurationStore;
    private readonly IGatewayStore _store;
    private readonly IDriverRegistry _drivers;
    private readonly GatewayRuntime _runtime;
    private readonly IGatewayLog _log;
    private readonly string _dataDirectory;
    private readonly bool _smokeTest;
    private readonly string? _smokeScreenshotPath;
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly List<Control> _configurationCommands = [];
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };
    private readonly Label _runState = UiTheme.Label("已停止", 10, UiTheme.Muted, true);
    private readonly Label _savedState = UiTheme.Label("配置已保存", 9, UiTheme.Muted);
    private readonly Label _footer = UiTheme.Label("就绪", 9, UiTheme.Muted);
    private readonly Label _deviceCount = UiTheme.Label("0 / 0", 26, UiTheme.Ink, true);
    private readonly Label _pointCount = UiTheme.Label("0", 26, UiTheme.Ink, true);
    private readonly Label _pendingCount = UiTheme.Label("0", 26, UiTheme.Ink, true);
    private readonly Label _mqttStatus = UiTheme.Label("未启用", 20, UiTheme.Muted, true);
    private readonly DataGridView _overviewGrid = UiTheme.Grid();
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private Button _saveButton = null!;
    private string _activePage = "overview";
    private bool _dirty, _busy, _refreshing, _closing, _allowClose;
    public bool SmokeTestFailed { get; private set; }

    public MainForm(GatewayConfiguration configuration, IConfigurationStore configurationStore,
        IGatewayStore store, IDriverRegistry drivers, GatewayRuntime runtime, IGatewayLog log,
        string dataDirectory, bool smokeTest = false, string? smokeScreenshotPath = null)
    {
        _configuration = Clone(configuration);
        _appliedMqtt = Clone(configuration.Mqtt);
        _configurationStore = configurationStore;
        _store = store;
        _drivers = drivers;
        _runtime = runtime;
        _log = log;
        _dataDirectory = dataDirectory;
        _smokeTest = smokeTest;
        _smokeScreenshotPath = smokeScreenshotPath;
        Text = "Edge Gateway · 边缘采集网关";
        Font = new Font("Microsoft YaHei UI", 10F);
        ForeColor = UiTheme.Ink;
        BackColor = UiTheme.Background;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        MinimumSize = new Size(1024, 700);
        Size = new Size(1400, 900);
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
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 186));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = UiTheme.Navy, Padding = new Padding(16, 22, 12, 16), ColumnCount = 1, RowCount = 9, Margin = new Padding(0) };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        brand.Controls.Add(UiTheme.Label("EDGE GATEWAY", 13, Color.White, true));
        brand.Controls.Add(UiTheme.Label("工业数据 · 本地连接", 9, Color.FromArgb(160, 178, 203)));
        sidebar.Controls.Add(brand, 0, 0);
        var entries = new[] { ("overview", "运行总览"), ("devices", "设备与点位"), ("realtime", "实时监控"), ("history", "历史与趋势"), ("cloud", "平台与系统"), ("logs", "运行日志") };
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            var button = new Button
            {
                Text = entry.Item2, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Navy, ForeColor = Color.FromArgb(190, 205, 225),
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(15, 0, 0, 0),
                Margin = new Padding(0, 0, 0, 6), Cursor = Cursors.Hand, Font = new Font(Font, FontStyle.Bold)
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => ShowPage(entry.Item1);
            _navigation[entry.Item1] = button;
            sidebar.Controls.Add(button, 0, i + 1);
        }
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        var edition = UiTheme.Label(".NET 8 · WinForms\n本地采集 / 可靠上报", 9, Color.FromArgb(142, 162, 190));
        edition.Dock = DockStyle.Bottom;
        sidebar.Controls.Add(edition, 0, 8);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.White, Padding = new Padding(22, 14, 14, 12), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var status = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        status.Controls.Add(_runState); status.Controls.Add(_savedState);
        _startButton = UiTheme.Button("启动采集", async (_, _) => await RunBusyAsync(StartCollectionAsync), true);
        _stopButton = UiTheme.Button("停止采集", async (_, _) => await RunBusyAsync(async () => { await _runtime.StopAsync(); SetFooter("采集已停止。"); }));
        _saveButton = UiTheme.Button("保存配置", async (_, _) => await RunBusyAsync(PersistConfigurationAsync));
        // In an AutoSize table column a wrapping FlowLayoutPanel reports a one-button
        // preferred width. Give header commands their full single-row preferred size.
        // Page toolbars intentionally keep wrapping to fit narrower content areas.
        var headerActions = new FlowLayoutPanel
        {
            Name = "HeaderActions", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, Dock = DockStyle.None,
            Margin = new Padding(16, 0, 0, 0), Padding = new Padding(0)
        };
        headerActions.Controls.AddRange(new Control[] { _startButton, _stopButton, _saveButton });
        header.Controls.Add(status, 0, 0);
        header.Controls.Add(headerActions, 1, 0);
        body.Controls.Add(header, 0, 0);
        body.Controls.Add(_pageHost, 0, 1);
        _footer.Dock = DockStyle.Fill; _footer.Padding = new Padding(22, 0, 0, 0);
        body.Controls.Add(_footer, 0, 2);
        shell.Controls.Add(sidebar, 0, 0); shell.Controls.Add(body, 1, 0);
        Controls.Add(shell);

        AddPage("overview", BuildOverviewPage());
        AddPage("devices", BuildDevicesPage());
        AddPage("realtime", BuildRealtimePage());
        AddPage("history", BuildHistoryPage());
        AddPage("cloud", BuildCloudPage());
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
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
        var cardInfo = new[] { ("在线 / 启用设备", _deviceCount, "独立连接，自动重连"), ("优质 / 最新采样", _pointCount, "质量与时间戳同步展示"), ("待补传消息", _pendingCount, "收到 MQTT 确认后移除"), ("平台连接", _mqttStatus, "QoS 1 · 持久化发送队列") };
        for (var i = 0; i < 4; i++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(17, 12, 10, 10), ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, i == 3 ? 0 : 12, 12) };
            card.Controls.Add(UiTheme.Label(cardInfo[i].Item1, 9, UiTheme.Muted));
            card.Controls.Add(cardInfo[i].Item2);
            card.Controls.Add(UiTheme.Label(cardInfo[i].Item3, 8, UiTheme.Muted));
            cards.Controls.Add(card, i, 0);
        }
        content.Controls.Add(cards, 0, 0);
        content.Controls.Add(UiTheme.Label("设备连接状态", 12, UiTheme.Ink, true), 0, 1);
        content.Controls.Add(_overviewGrid, 0, 2);
        var help = UiTheme.Label("首次使用：直接启动内置模拟设备，观察实时数据；添加真实设备后，按现场点表配置协议、地址和字节序。", 9, UiTheme.Muted);
        help.Dock = DockStyle.Fill; help.MaximumSize = new Size(1200, 0);
        content.Controls.Add(help, 0, 3);
        return UiTheme.PageLayout("运行总览", "从设备现场到数据平台，掌握每条采集链路的状态。", UiTheme.Toolbar(), content);
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
                AssertHeaderActionsFit();
                Size = originalSize;
                foreach (var key in _pages.Keys)
                {
                    ShowPage(key);
                    PerformLayoutRecursively(this);
                    AssertHeaderActionsFit();
                    AssertPageToolbarsFit(_pages[key], key);
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
            }
            catch (Exception exception) { SmokeTestFailed = true; Console.Error.WriteLine(exception); }
            _allowClose = true; Close();
            return;
        }
        _refreshTimer.Start();
        await RefreshSnapshotsAsync();
        if (_configuration.AutoStart) await RunBusyAsync(StartCollectionAsync);
    }

    private static void PerformLayoutRecursively(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls)
            if (child.Visible) PerformLayoutRecursively(child);
        root.PerformLayout();
    }

    private void AssertHeaderActionsFit()
    {
        var buttons = new[] { _startButton, _stopButton, _saveButton };
        foreach (var button in buttons) AssertVisibleBounds(button, "header");
        var rectangles = buttons.Select(x => x.Parent!.RectangleToScreen(x.Bounds)).ToArray();
        if (rectangles.Any(x => x.Top != rectangles[0].Top) || rectangles[0].Right > rectangles[1].Left || rectangles[1].Right > rectangles[2].Left)
            throw new InvalidOperationException("Header commands are wrapped or overlap; Start, Stop and Save must share one accessible row.");
    }

    private static void AssertPageToolbarsFit(Control root, string page)
    {
        if (root is FlowLayoutPanel toolbar)
        {
            foreach (Control child in toolbar.Controls)
                if (child.Visible) AssertVisibleBounds(child, page + " toolbar");
        }
        foreach (Control child in root.Controls)
            if (child.Visible) AssertPageToolbarsFit(child, page);
    }

    private static void AssertVisibleBounds(Control control, string context)
    {
        if (!control.Visible || control.Width <= 0 || control.Height <= 0)
            throw new InvalidOperationException($"{context}: control '{control.Text}' has no visible area.");
        var bounds = control.Parent!.RectangleToScreen(control.Bounds);
        for (var ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            var viewport = ancestor.RectangleToScreen(ancestor.ClientRectangle);
            if (!viewport.Contains(bounds))
                throw new InvalidOperationException($"{context}: '{control.Text}' ({bounds}) is clipped by {ancestor.GetType().Name} ({viewport}).");
        }
    }

    private async Task StartCollectionAsync()
    {
        await PersistConfigurationAsync();
        await _runtime.StartAsync();
        SetFooter("采集已启动，可在实时监控中查看数据。运行期间配置只读。");
    }

    private async Task PersistConfigurationAsync()
    {
        if (_runtime.IsRunning) throw new InvalidOperationException("请先停止采集，再保存配置。");
        var errors = ConfigurationValidator.Validate(_configuration, _drivers);
        if (errors.Count > 0) throw new InvalidOperationException("配置检查未通过：\n\n" + string.Join("\n", errors));
        if (MqttDestinationChanged(_appliedMqtt, _configuration.Mqtt))
        {
            var statistics = await _store.GetStatisticsAsync();
            if (statistics.OutboxCount > 0)
            {
                var response = MessageBox.Show(this,
                    $"当前仍有 {statistics.OutboxCount:N0} 条待发送消息。\n\n"
                    + $"原连接：{_appliedMqtt.Host}:{_appliedMqtt.Port}\n新连接：{_configuration.Mqtt.Host}:{_configuration.Mqtt.Port}\n\n"
                    + "保存后，这些消息会在平台启用时使用新的连接与凭据发送，消息中仍保留原网关标识和原主题。\n\n确认将待发送消息迁移到新连接？选择“否”可先使用原连接完成补传。",
                    "确认待发送消息的目标变更", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (response != DialogResult.Yes) throw new OperationCanceledException("已取消 MQTT 连接变更。");
            }
        }
        await _configurationStore.SaveAsync(_configuration);
        _runtime.Configure(_configuration);
        _appliedMqtt = Clone(_configuration.Mqtt);
        _dirty = false;
        SetFooter("配置已保存，自动保留上一版备份。");
        UpdateCommands();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy || _closing) return;
        _busy = true; UpdateCommands(); UseWaitCursor = true;
        try { await action(); }
        catch (OperationCanceledException) { SetFooter("操作已取消。"); }
        catch (Exception exception) { ShowError(exception.Message); }
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
            var statistics = await _store.GetStatisticsAsync();
            if (!IsDisposed) _pendingCount.Text = statistics.OutboxCount.ToString("N0", CultureInfo.CurrentCulture);
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
        _startButton.Enabled = !_busy && !running;
        _stopButton.Enabled = !_busy && running;
        _saveButton.Enabled = !_busy && !running;
        foreach (var control in _configurationCommands) control.Enabled = !_busy && !running;
        _runState.Text = _busy ? "正在处理…" : running ? "● 采集运行中" : "● 采集已停止";
        _runState.ForeColor = running ? UiTheme.Green : UiTheme.Muted;
        _savedState.Text = _dirty ? "配置已修改 · 请保存" : $"{_configuration.Name}  /  {_configuration.GatewayId}";
        _savedState.ForeColor = _dirty ? Color.FromArgb(180, 83, 9) : UiTheme.Muted;
        UpdateWriteButton();
    }

    private void MarkDirty() { _dirty = true; UpdateCommands(); }
    private void SetFooter(string message) { if (!IsDisposed) _footer.Text = message; }
    private void ShowError(string message)
    {
        SetFooter(message.Split('\n')[0]);
        if (!_smokeTest) MessageBox.Show(this, message, "采集网关", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        if (_busy) { SetFooter("正在处理当前操作，请稍后关闭窗口。"); return; }
        if (_dirty)
        {
            var answer = MessageBox.Show(this, "配置已修改，是否保存后退出？", "保存配置", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes)
            {
                try { await PersistConfigurationAsync(); }
                catch (Exception exception) { ShowError(exception.Message); return; }
            }
        }
        _closing = true; _refreshTimer.Stop(); Enabled = false;
        try
        {
            await _runtime.StopAsync();
            _allowClose = true; Close();
        }
        catch (Exception exception)
        {
            _closing = false; Enabled = true; _refreshTimer.Start(); ShowError("停止采集失败：" + exception.Message);
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
        || previous.Username != next.Username || previous.ClientId != next.ClientId || previous.PasswordEnvironmentVariable != next.PasswordEnvironmentVariable;
    private static string StateText(DeviceState state) => state switch
    {
        DeviceState.Running => "运行中", DeviceState.Connecting => "连接中", DeviceState.Reconnecting => "重连中", DeviceState.Faulted => "异常", _ => "已停止"
    };
    private static string QualityText(SampleQuality quality) => quality switch { SampleQuality.Good => "正常", SampleQuality.Bad => "异常", _ => "不确定" };
    private static string ValueText(object? value) => value switch
    {
        null => "—", bool boolean => boolean ? "true" : "false", double number => number.ToString("G9", CultureInfo.InvariantCulture),
        float number => number.ToString("G7", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—"
    };
    private sealed record Choice(Guid? Id, string Name) { public override string ToString() => Name; }
}
