using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private System.ComponentModel.IContainer? components;
    private TableLayoutPanel applicationShell = null!;
    private Panel navigationViewportPanel = null!;
    private TableLayoutPanel navigationContentPanel = null!;
    private TableLayoutPanel applicationBody = null!;
    private TableLayoutPanel applicationHeader = null!;
    private FlowLayoutPanel headerStatusPanel = null!;
    private FlowLayoutPanel headerSaveStatusPanel = null!;
    private Label brandTitleLabel = null!;
    private Label brandCaptionLabel = null!;
    private Label navigationEditionLabel = null!;
    private AntdUI.Button navigationOverviewButton = null!;
    private AntdUI.Button navigationDevicesButton = null!;
    private AntdUI.Button navigationRealtimeButton = null!;
    private AntdUI.Button navigationHistoryButton = null!;
    private AntdUI.Button navigationDiagnosticsButton = null!;
    private AntdUI.Button navigationDatabaseButton = null!;
    private AntdUI.Button navigationCloudButton = null!;
    private AntdUI.Button navigationLogsButton = null!;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        applicationShell = new TableLayoutPanel { Name = "ApplicationShell", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        navigationViewportPanel = new Panel { Name = "NavigationViewport", Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Navy, Margin = Padding.Empty };
        navigationContentPanel = new TableLayoutPanel { Name = "NavigationContent", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = UiTheme.Navy, Padding = new Padding(12, 18, 10, 16), ColumnCount = 1, RowCount = 10, Margin = Padding.Empty };
        applicationBody = new TableLayoutPanel { Name = "ApplicationBody", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        applicationHeader = new TableLayoutPanel { Name = "ApplicationHeader", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.White, Padding = new Padding(18, 8, 12, 8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        headerStatusPanel = new FlowLayoutPanel { Name = "HeaderStatus", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = Padding.Empty };
        headerSaveStatusPanel = new FlowLayoutPanel { Name = "HeaderSaveStatus", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Anchor = AnchorStyles.Top | AnchorStyles.Right, Dock = DockStyle.None, Margin = new Padding(12, 0, 0, 0) };
        brandTitleLabel = new Label { Name = "NavigationBrandTitle", Text = "EDGE GATEWAY", AutoSize = true, Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold), ForeColor = Color.White };
        brandCaptionLabel = new Label { Name = "NavigationBrandCaption", Text = "工业数据 · 本地连接", AutoSize = true, Font = new Font("Microsoft YaHei UI", 10F), ForeColor = Color.FromArgb(160, 178, 203) };
        navigationEditionLabel = new Label { Name = "NavigationEdition", Text = "边缘数据工作台\n本地采集 / 可靠上报", Dock = DockStyle.Fill, AutoSize = true, Font = new Font("Microsoft YaHei UI", 10F), ForeColor = Color.FromArgb(142, 162, 190), Margin = new Padding(0, 20, 0, 0) };
        navigationOverviewButton = CreateNavigationButton("运行总览", "navigationOverviewButton");
        navigationDevicesButton = CreateNavigationButton("设备与点位", "navigationDevicesButton");
        navigationRealtimeButton = CreateNavigationButton("实时监控", "navigationRealtimeButton");
        navigationHistoryButton = CreateNavigationButton("历史与趋势", "navigationHistoryButton");
        navigationDiagnosticsButton = CreateNavigationButton("通信诊断", "navigationDiagnosticsButton");
        navigationDatabaseButton = CreateNavigationButton("数据库", "navigationDatabaseButton");
        navigationCloudButton = CreateNavigationButton("平台与系统", "navigationCloudButton");
        navigationLogsButton = CreateNavigationButton("运行日志", "navigationLogsButton");
        _pageHost = new Panel { Name = "PageHost", Dock = DockStyle.Fill, Margin = Padding.Empty };
        _runState = UiTheme.Label("已停止", 10, UiTheme.Muted, true);
        _savedState = UiTheme.Label("配置已保存", 9, UiTheme.Muted);
        _footer = UiTheme.Label("就绪", 10, UiTheme.Muted);
        _automaticSaveState = UiTheme.Label("配置自动保存", 9, UiTheme.Muted);

        applicationShell.SuspendLayout();
        navigationViewportPanel.SuspendLayout();
        navigationContentPanel.SuspendLayout();
        applicationBody.SuspendLayout();
        applicationHeader.SuspendLayout();
        headerStatusPanel.SuspendLayout();
        headerSaveStatusPanel.SuspendLayout();
        SuspendLayout();

        applicationShell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 206F));
        applicationShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        applicationShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        navigationContentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 10; i++) navigationContentPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var brandPanel = new FlowLayoutPanel { Name = "NavigationBrand", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 20) };
        brandPanel.Controls.Add(brandTitleLabel);
        brandPanel.Controls.Add(brandCaptionLabel);
        navigationContentPanel.Controls.Add(brandPanel, 0, 0);

        AntdUI.Button[] navButtons = { navigationOverviewButton, navigationDevicesButton, navigationRealtimeButton, navigationHistoryButton, navigationDiagnosticsButton, navigationDatabaseButton, navigationCloudButton, navigationLogsButton };
        for (int i = 0; i < navButtons.Length; i++)
        {
            navButtons[i].Dock = DockStyle.Fill;
            navButtons[i].AutoSize = true;
            navButtons[i].MinimumSize = new Size(0, 48);
            navButtons[i].Margin = new Padding(0, 0, 0, 6);
            navigationContentPanel.Controls.Add(navButtons[i], 0, i + 1);
        }
        navigationContentPanel.Controls.Add(navigationEditionLabel, 0, 9);
        navigationViewportPanel.Controls.Add(navigationContentPanel);

        applicationBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        applicationBody.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        applicationBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        applicationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        _runState.Margin = new Padding(0, 4, 16, 4);
        headerStatusPanel.Controls.Add(_runState);
        headerStatusPanel.Controls.Add(_savedState);
        _automaticSaveState.Name = "AutomaticSaveState";
        headerSaveStatusPanel.Controls.Add(_automaticSaveState);
        applicationHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        applicationHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        applicationHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        applicationHeader.Controls.Add(headerStatusPanel, 0, 0);
        applicationHeader.Controls.Add(headerSaveStatusPanel, 1, 0);

        _footer.Name = "OperationFeedback";
        _footer.Dock = DockStyle.Fill;
        _footer.Padding = new Padding(16, 0, 12, 0);
        _footer.AutoEllipsis = true;
        _footer.BackColor = Color.White;
        _footer.Cursor = Cursors.Hand;
        _footer.TextAlign = ContentAlignment.MiddleLeft;
        _footer.AccessibleName = "最近操作结果，点击查看完整说明";
        _footer.Click += (_, _) => MessageBox.Show(this, _lastFeedback, "最近操作结果", MessageBoxButtons.OK, MessageBoxIcon.Information);

        applicationBody.Controls.Add(applicationHeader, 0, 0);
        applicationBody.Controls.Add(_pageHost, 0, 1);
        applicationBody.Controls.Add(_footer, 0, 2);
        applicationShell.Controls.Add(navigationViewportPanel, 0, 0);
        applicationShell.Controls.Add(applicationBody, 1, 0);
        Controls.Add(applicationShell);

        applicationShell.ResumeLayout(false);
        navigationContentPanel.ResumeLayout(false);
        navigationContentPanel.PerformLayout();
        navigationViewportPanel.ResumeLayout(false);
        applicationHeader.ResumeLayout(false);
        applicationHeader.PerformLayout();
        headerStatusPanel.ResumeLayout(false);
        headerStatusPanel.PerformLayout();
        headerSaveStatusPanel.ResumeLayout(false);
        headerSaveStatusPanel.PerformLayout();
        applicationBody.ResumeLayout(false);
        applicationBody.PerformLayout();
        ResumeLayout(false);
    }

    private static AntdUI.Button CreateNavigationButton(string text, string name) => new()
    {
        Name = name,
        Text = text,
        Font = new Font("Microsoft YaHei UI", 10F),
        ForeColor = Color.FromArgb(190, 205, 225),
        BackColor = UiTheme.Navy,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(12, 0, 8, 0),
        Cursor = Cursors.Hand,
        Radius = 6
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }
}
