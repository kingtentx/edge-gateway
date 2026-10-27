#nullable enable
using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private TableLayoutPanel applicationShell = null!;
    private Panel navigationViewportPanel = null!;
    private TableLayoutPanel navigationContentPanel = null!;
    private FlowLayoutPanel navigationBrandPanel = null!;
    private TableLayoutPanel applicationBody = null!;
    private TableLayoutPanel applicationHeader = null!;
    private FlowLayoutPanel headerStatusPanel = null!;
    private FlowLayoutPanel headerSaveStatusPanel = null!;
    private Label brandTitleLabel = null!;
    private Label brandCaptionLabel = null!;

    private void InitializeComponent()
    {
        applicationShell = new TableLayoutPanel();
        navigationViewportPanel = new Panel();
        navigationContentPanel = new TableLayoutPanel();
        navigationBrandPanel = new FlowLayoutPanel();
        brandTitleLabel = new Label();
        brandCaptionLabel = new Label();
        navigationOverviewButton = new GatewayButton();
        navigationDevicesButton = new GatewayButton();
        navigationRealtimeButton = new GatewayButton();
        navigationHistoryButton = new GatewayButton();
        navigationDiagnosticsButton = new GatewayButton();
        navigationDatabaseButton = new GatewayButton();
        navigationCloudButton = new GatewayButton();
        navigationLogsButton = new GatewayButton();
        applicationBody = new TableLayoutPanel();
        applicationHeader = new TableLayoutPanel();
        headerStatusPanel = new FlowLayoutPanel();
        _runState = new Label();
        _savedState = new Label();
        headerSaveStatusPanel = new FlowLayoutPanel();
        _automaticSaveState = new Label();
        _pageHost = new Panel();
        _footer = new Label();
        applicationShell.SuspendLayout();
        navigationViewportPanel.SuspendLayout();
        navigationContentPanel.SuspendLayout();
        navigationBrandPanel.SuspendLayout();
        applicationBody.SuspendLayout();
        applicationHeader.SuspendLayout();
        headerStatusPanel.SuspendLayout();
        headerSaveStatusPanel.SuspendLayout();
        SuspendLayout();
        //
        // applicationShell
        //
        applicationShell.ColumnCount = 2;
        applicationShell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 206F));
        applicationShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        applicationShell.Controls.Add(navigationViewportPanel, 0, 0);
        applicationShell.Controls.Add(applicationBody, 1, 0);
        applicationShell.Dock = DockStyle.Fill;
        applicationShell.Location = new Point(0, 0);
        applicationShell.Margin = new Padding(0);
        applicationShell.Name = "applicationShell";
        applicationShell.RowCount = 1;
        applicationShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        applicationShell.Size = new Size(1400, 860);
        applicationShell.TabIndex = 0;
        //
        // navigationViewportPanel
        //
        navigationViewportPanel.AutoScroll = true;
        navigationViewportPanel.BackColor = Color.White;
        navigationViewportPanel.Controls.Add(navigationContentPanel);
        navigationViewportPanel.Dock = DockStyle.Fill;
        navigationViewportPanel.Location = new Point(0, 0);
        navigationViewportPanel.Margin = new Padding(0);
        navigationViewportPanel.Name = "navigationViewportPanel";
        navigationViewportPanel.Size = new Size(206, 860);
        navigationViewportPanel.TabIndex = 0;
        //
        // navigationContentPanel
        //
        navigationContentPanel.AutoSize = true;
        navigationContentPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        navigationContentPanel.BackColor = Color.White;
        navigationContentPanel.ColumnCount = 1;
        navigationContentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        navigationContentPanel.Controls.Add(navigationBrandPanel, 0, 0);
        navigationContentPanel.Controls.Add(navigationOverviewButton, 0, 1);
        navigationContentPanel.Controls.Add(navigationDevicesButton, 0, 2);
        navigationContentPanel.Controls.Add(navigationRealtimeButton, 0, 3);
        navigationContentPanel.Controls.Add(navigationHistoryButton, 0, 4);
        navigationContentPanel.Controls.Add(navigationDiagnosticsButton, 0, 5);
        navigationContentPanel.Controls.Add(navigationDatabaseButton, 0, 6);
        navigationContentPanel.Controls.Add(navigationCloudButton, 0, 7);
        navigationContentPanel.Controls.Add(navigationLogsButton, 0, 8);
        navigationContentPanel.Dock = DockStyle.Top;
        navigationContentPanel.Location = new Point(0, 0);
        navigationContentPanel.Margin = new Padding(0);
        navigationContentPanel.Name = "navigationContentPanel";
        navigationContentPanel.Padding = new Padding(8, 16, 8, 12);
        navigationContentPanel.RowCount = 10;
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.RowStyles.Add(new RowStyle());
        navigationContentPanel.Size = new Size(206, 531);
        navigationContentPanel.TabIndex = 0;
        //
        // navigationBrandPanel
        //
        navigationBrandPanel.AutoSize = true;
        navigationBrandPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        navigationBrandPanel.Controls.Add(brandTitleLabel);
        navigationBrandPanel.Controls.Add(brandCaptionLabel);
        navigationBrandPanel.Dock = DockStyle.Fill;
        navigationBrandPanel.FlowDirection = FlowDirection.TopDown;
        navigationBrandPanel.Location = new Point(12, 18);
        navigationBrandPanel.Margin = new Padding(4, 0, 0, 16);
        navigationBrandPanel.Name = "navigationBrandPanel";
        navigationBrandPanel.Size = new Size(184, 45);
        navigationBrandPanel.TabIndex = 0;
        navigationBrandPanel.WrapContents = false;
        //
        // brandTitleLabel
        //
        brandTitleLabel.AutoSize = true;
        brandTitleLabel.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        brandTitleLabel.ForeColor = UiTheme.Blue;
        brandTitleLabel.Location = new Point(3, 0);
        brandTitleLabel.Name = "brandTitleLabel";
        brandTitleLabel.Size = new Size(159, 25);
        brandTitleLabel.TabIndex = 0;
        brandTitleLabel.Text = "EDGE GATEWAY";
        //
        // brandCaptionLabel
        //
        brandCaptionLabel.AutoSize = true;
        brandCaptionLabel.Font = new Font("Microsoft YaHei UI", 10F);
        brandCaptionLabel.ForeColor = UiTheme.Muted;
        brandCaptionLabel.Location = new Point(3, 25);
        brandCaptionLabel.Name = "brandCaptionLabel";
        brandCaptionLabel.Size = new Size(132, 20);
        brandCaptionLabel.TabIndex = 1;
        brandCaptionLabel.Text = "工业数据 · 本地连接";
        //
        // navigationOverviewButton
        //
        navigationOverviewButton.Cursor = Cursors.Hand;
        navigationOverviewButton.DefaultBack = Color.White;
        navigationOverviewButton.IconSvg = "DashboardOutlined";
        navigationOverviewButton.Radius = 6;
        navigationOverviewButton.TextAlign = ContentAlignment.MiddleLeft;
        navigationOverviewButton.BackHover = UiTheme.SurfaceSubtle;
        navigationOverviewButton.BackActive = UiTheme.BlueSoft;
        navigationOverviewButton.Dock = DockStyle.Fill;
        navigationOverviewButton.ForeColor = UiTheme.Ink;
        navigationOverviewButton.Location = new Point(12, 83);
        navigationOverviewButton.Margin = new Padding(0, 0, 0, 4);
        navigationOverviewButton.MinimumSize = new Size(0, 42);
        navigationOverviewButton.Name = "navigationOverviewButton";
        navigationOverviewButton.Padding = new Padding(12, 0, 8, 0);
        navigationOverviewButton.Size = new Size(184, 42);
        navigationOverviewButton.TabIndex = 0;
        navigationOverviewButton.Text = "运行总览";
        navigationOverviewButton.TextAlign = ContentAlignment.MiddleLeft;
        //
        // navigationDevicesButton
        //
        navigationDevicesButton.Cursor = Cursors.Hand;
        navigationDevicesButton.DefaultBack = Color.White;
        navigationDevicesButton.IconSvg = "ApartmentOutlined";
        navigationDevicesButton.Radius = 6;
        navigationDevicesButton.TextAlign = ContentAlignment.MiddleLeft;
        navigationDevicesButton.BackHover = UiTheme.SurfaceSubtle;
        navigationDevicesButton.BackActive = UiTheme.BlueSoft;
        navigationDevicesButton.Dock = DockStyle.Fill;
        navigationDevicesButton.ForeColor = UiTheme.Ink;
        navigationDevicesButton.Location = new Point(12, 137);
        navigationDevicesButton.Margin = new Padding(0, 0, 0, 4);
        navigationDevicesButton.MinimumSize = new Size(0, 42);
        navigationDevicesButton.Name = "navigationDevicesButton";
        navigationDevicesButton.Padding = new Padding(12, 0, 8, 0);
        navigationDevicesButton.Size = new Size(184, 42);
        navigationDevicesButton.TabIndex = 1;
        navigationDevicesButton.Text = "设备与点位";
        navigationDevicesButton.TextAlign = ContentAlignment.MiddleLeft;
        //
        // navigationRealtimeButton
        //
        navigationRealtimeButton.Cursor = Cursors.Hand;
        navigationRealtimeButton.DefaultBack = Color.White;
        navigationRealtimeButton.IconSvg = "FundOutlined";
        navigationRealtimeButton.Radius = 6;
        navigationRealtimeButton.TextAlign = ContentAlignment.MiddleLeft;
        navigationRealtimeButton.BackHover = UiTheme.SurfaceSubtle;
        navigationRealtimeButton.BackActive = UiTheme.BlueSoft;
        navigationRealtimeButton.Dock = DockStyle.Fill;
        navigationRealtimeButton.ForeColor = UiTheme.Ink;
        navigationRealtimeButton.Location = new Point(12, 191);
        navigationRealtimeButton.Margin = new Padding(0, 0, 0, 4);
        navigationRealtimeButton.MinimumSize = new Size(0, 42);
        navigationRealtimeButton.Name = "navigationRealtimeButton";
        navigationRealtimeButton.Padding = new Padding(12, 0, 8, 0);
        navigationRealtimeButton.Size = new Size(184, 42);
        navigationRealtimeButton.TabIndex = 2;
        navigationRealtimeButton.Text = "实时监控";
        navigationRealtimeButton.TextAlign = ContentAlignment.MiddleLeft;
        //
        // navigationHistoryButton
        //
        navigationHistoryButton.Cursor = Cursors.Hand;
        navigationHistoryButton.DefaultBack = Color.White;
        navigationHistoryButton.IconSvg = "LineChartOutlined";
        navigationHistoryButton.Radius = 6;
        navigationHistoryButton.TextAlign = ContentAlignment.MiddleLeft;
        navigationHistoryButton.BackHover = UiTheme.SurfaceSubtle;
        navigationHistoryButton.BackActive = UiTheme.BlueSoft;
        navigationHistoryButton.Dock = DockStyle.Fill;
        navigationHistoryButton.ForeColor = UiTheme.Ink;
        navigationHistoryButton.Location = new Point(12, 245);
        navigationHistoryButton.Margin = new Padding(0, 0, 0, 4);
        navigationHistoryButton.MinimumSize = new Size(0, 42);
        navigationHistoryButton.Name = "navigationHistoryButton";
        navigationHistoryButton.Padding = new Padding(12, 0, 8, 0);
        navigationHistoryButton.Size = new Size(184, 42);
        navigationHistoryButton.TabIndex = 3;
        navigationHistoryButton.Text = "历史与趋势";
        navigationHistoryButton.TextAlign = ContentAlignment.MiddleLeft;
        //
        // navigationDiagnosticsButton
        //
        navigationDiagnosticsButton.Cursor = Cursors.Hand;
        navigationDiagnosticsButton.DefaultBack = Color.White;
        navigationDiagnosticsButton.IconSvg = "ApiOutlined";
        navigationDiagnosticsButton.Radius = 6;
        navigationDiagnosticsButton.TextAlign = ContentAlignment.MiddleLeft;
        navigationDiagnosticsButton.BackHover = UiTheme.SurfaceSubtle;
        navigationDiagnosticsButton.BackActive = UiTheme.BlueSoft;
        navigationDiagnosticsButton.Dock = DockStyle.Fill;
        navigationDiagnosticsButton.ForeColor = UiTheme.Ink;
        navigationDiagnosticsButton.Location = new Point(12, 299);
        navigationDiagnosticsButton.Margin = new Padding(0, 0, 0, 4);
        navigationDiagnosticsButton.MinimumSize = new Size(0, 42);
        navigationDiagnosticsButton.Name = "navigationDiagnosticsButton";
        navigationDiagnosticsButton.Padding = new Padding(12, 0, 8, 0);
        navigationDiagnosticsButton.Size = new Size(184, 42);
        navigationDiagnosticsButton.TabIndex = 4;
        navigationDiagnosticsButton.Text = "通信诊断";
        navigationDiagnosticsButton.TextAlign = ContentAlignment.MiddleLeft;
        //
        // navigationDatabaseButton
        //
        navigationDatabaseButton.Cursor = Cursors.Hand;
        navigationDatabaseButton.DefaultBack = Color.White;
        navigationDatabaseButton.IconSvg = "DatabaseOutlined";
        navigationDatabaseButton.Radius = 6;
        navigationDatabaseButton.TextAlign = ContentAlignment.MiddleLeft;
        navigationDatabaseButton.BackHover = UiTheme.SurfaceSubtle;
        navigationDatabaseButton.BackActive = UiTheme.BlueSoft;
        navigationDatabaseButton.Dock = DockStyle.Fill;
        navigationDatabaseButton.ForeColor = UiTheme.Ink;
        navigationDatabaseButton.Location = new Point(12, 353);
        navigationDatabaseButton.Margin = new Padding(0, 0, 0, 4);
        navigationDatabaseButton.MinimumSize = new Size(0, 42);
        navigationDatabaseButton.Name = "navigationDatabaseButton";
        navigationDatabaseButton.Padding = new Padding(12, 0, 8, 0);
        navigationDatabaseButton.Size = new Size(184, 42);
        navigationDatabaseButton.TabIndex = 5;
        navigationDatabaseButton.Text = "数据库";
        navigationDatabaseButton.TextAlign = ContentAlignment.MiddleLeft;
        //
        // navigationCloudButton
        //
        navigationCloudButton.Cursor = Cursors.Hand;
        navigationCloudButton.DefaultBack = Color.White;
        navigationCloudButton.IconSvg = "CloudOutlined";
        navigationCloudButton.Radius = 6;
        navigationCloudButton.TextAlign = ContentAlignment.MiddleLeft;
        navigationCloudButton.BackHover = UiTheme.SurfaceSubtle;
        navigationCloudButton.BackActive = UiTheme.BlueSoft;
        navigationCloudButton.Dock = DockStyle.Fill;
        navigationCloudButton.ForeColor = UiTheme.Ink;
        navigationCloudButton.Location = new Point(12, 407);
        navigationCloudButton.Margin = new Padding(0, 0, 0, 4);
        navigationCloudButton.MinimumSize = new Size(0, 42);
        navigationCloudButton.Name = "navigationCloudButton";
        navigationCloudButton.Padding = new Padding(12, 0, 8, 0);
        navigationCloudButton.Size = new Size(184, 42);
        navigationCloudButton.TabIndex = 6;
        navigationCloudButton.Text = "平台与系统";
        navigationCloudButton.TextAlign = ContentAlignment.MiddleLeft;
        //
        // navigationLogsButton
        //
        navigationLogsButton.Cursor = Cursors.Hand;
        navigationLogsButton.DefaultBack = Color.White;
        navigationLogsButton.IconSvg = "FileTextOutlined";
        navigationLogsButton.Radius = 6;
        navigationLogsButton.TextAlign = ContentAlignment.MiddleLeft;
        navigationLogsButton.BackHover = UiTheme.SurfaceSubtle;
        navigationLogsButton.BackActive = UiTheme.BlueSoft;
        navigationLogsButton.Dock = DockStyle.Fill;
        navigationLogsButton.ForeColor = UiTheme.Ink;
        navigationLogsButton.Location = new Point(12, 461);
        navigationLogsButton.Margin = new Padding(0, 0, 0, 4);
        navigationLogsButton.MinimumSize = new Size(0, 42);
        navigationLogsButton.Name = "navigationLogsButton";
        navigationLogsButton.Padding = new Padding(12, 0, 8, 0);
        navigationLogsButton.Size = new Size(184, 42);
        navigationLogsButton.TabIndex = 7;
        navigationLogsButton.Text = "运行日志";
        navigationLogsButton.TextAlign = ContentAlignment.MiddleLeft;
        //
        // applicationBody
        //
        applicationBody.ColumnCount = 1;
        applicationBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        applicationBody.Controls.Add(applicationHeader, 0, 0);
        applicationBody.Controls.Add(_pageHost, 0, 1);
        applicationBody.Controls.Add(_footer, 0, 2);
        applicationBody.Dock = DockStyle.Fill;
        applicationBody.Location = new Point(206, 0);
        applicationBody.Margin = new Padding(0);
        applicationBody.Name = "applicationBody";
        applicationBody.RowCount = 3;
        applicationBody.RowStyles.Add(new RowStyle());
        applicationBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        applicationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        applicationBody.Size = new Size(1194, 860);
        applicationBody.TabIndex = 1;
        //
        // applicationHeader
        //
        applicationHeader.AutoSize = true;
        applicationHeader.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        applicationHeader.BackColor = Color.White;
        applicationHeader.ColumnCount = 2;
        applicationHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        applicationHeader.ColumnStyles.Add(new ColumnStyle());
        applicationHeader.Controls.Add(headerStatusPanel, 0, 0);
        applicationHeader.Controls.Add(headerSaveStatusPanel, 1, 0);
        applicationHeader.Dock = DockStyle.Fill;
        applicationHeader.Location = new Point(0, 0);
        applicationHeader.Margin = new Padding(0);
        applicationHeader.Name = "applicationHeader";
        applicationHeader.Padding = new Padding(16, 6, 12, 6);
        applicationHeader.RowCount = 1;
        applicationHeader.RowStyles.Add(new RowStyle());
        applicationHeader.Size = new Size(1194, 71);
        applicationHeader.TabIndex = 0;
        //
        // headerStatusPanel
        //
        headerStatusPanel.AutoSize = true;
        headerStatusPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        headerStatusPanel.Controls.Add(_runState);
        headerStatusPanel.Controls.Add(_savedState);
        headerStatusPanel.Dock = DockStyle.Fill;
        headerStatusPanel.Location = new Point(18, 8);
        headerStatusPanel.Margin = new Padding(0);
        headerStatusPanel.Name = "headerStatusPanel";
        headerStatusPanel.Size = new Size(1059, 55);
        headerStatusPanel.TabIndex = 0;
        //
        // _runState
        //
        _runState.AutoSize = true;
        _runState.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _runState.ForeColor = UiTheme.Muted;
        _runState.Location = new Point(0, 4);
        _runState.Margin = new Padding(0, 4, 16, 4);
        _runState.Name = "_runState";
        _runState.Size = new Size(51, 19);
        _runState.TabIndex = 0;
        _runState.Text = "已停止";
        //
        // _savedState
        //
        _savedState.AutoSize = true;
        _savedState.ForeColor = UiTheme.Muted;
        _savedState.Location = new Point(67, 4);
        _savedState.Margin = new Padding(0, 4, 0, 4);
        _savedState.Name = "_savedState";
        _savedState.Size = new Size(79, 20);
        _savedState.TabIndex = 1;
        _savedState.Text = "配置已保存";
        //
        // headerSaveStatusPanel
        //
        headerSaveStatusPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        headerSaveStatusPanel.AutoSize = true;
        headerSaveStatusPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        headerSaveStatusPanel.Controls.Add(_automaticSaveState);
        headerSaveStatusPanel.Location = new Point(1089, 8);
        headerSaveStatusPanel.Margin = new Padding(12, 0, 0, 0);
        headerSaveStatusPanel.Name = "headerSaveStatusPanel";
        headerSaveStatusPanel.Size = new Size(93, 28);
        headerSaveStatusPanel.TabIndex = 1;
        headerSaveStatusPanel.WrapContents = false;
        //
        // _automaticSaveState
        //
        _automaticSaveState.AutoSize = true;
        _automaticSaveState.ForeColor = UiTheme.Muted;
        _automaticSaveState.Location = new Point(0, 4);
        _automaticSaveState.Margin = new Padding(0, 4, 0, 4);
        _automaticSaveState.Name = "_automaticSaveState";
        _automaticSaveState.Size = new Size(93, 20);
        _automaticSaveState.TabIndex = 0;
        _automaticSaveState.Text = "配置自动保存";
        //
        // _pageHost
        //
        _pageHost.Dock = DockStyle.Fill;
        _pageHost.Location = new Point(0, 71);
        _pageHost.Margin = new Padding(0);
        _pageHost.Name = "_pageHost";
        _pageHost.Size = new Size(1194, 755);
        _pageHost.TabIndex = 1;
        //
        // _footer
        //
        _footer.AccessibleName = "最近操作结果，点击查看完整说明";
        _footer.AutoEllipsis = true;
        _footer.BackColor = Color.White;
        _footer.Cursor = Cursors.Hand;
        _footer.Dock = DockStyle.Fill;
        _footer.ForeColor = UiTheme.Muted;
        _footer.Location = new Point(0, 830);
        _footer.Margin = new Padding(0, 4, 0, 4);
        _footer.Name = "_footer";
        _footer.Padding = new Padding(16, 0, 12, 0);
        _footer.Size = new Size(1194, 26);
        _footer.TabIndex = 2;
        _footer.Text = "就绪";
        _footer.TextAlign = ContentAlignment.MiddleLeft;
        _footer.Click += OperationFeedbackClicked;
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = UiTheme.Background;
        ClientSize = new Size(1400, 860);
        Controls.Add(applicationShell);
        ForeColor = UiTheme.Ink;
        MinimumSize = new Size(496, 319);
        Name = "MainForm";
        Text = "Edge Gateway · 边缘采集网关";
        applicationShell.ResumeLayout(false);
        navigationViewportPanel.ResumeLayout(false);
        navigationViewportPanel.PerformLayout();
        navigationContentPanel.ResumeLayout(false);
        navigationContentPanel.PerformLayout();
        navigationBrandPanel.ResumeLayout(false);
        navigationBrandPanel.PerformLayout();
        applicationBody.ResumeLayout(false);
        applicationBody.PerformLayout();
        applicationHeader.ResumeLayout(false);
        applicationHeader.PerformLayout();
        headerStatusPanel.ResumeLayout(false);
        headerStatusPanel.PerformLayout();
        headerSaveStatusPanel.ResumeLayout(false);
        headerSaveStatusPanel.PerformLayout();
        ResumeLayout(false);
    }

    private GatewayButton navigationOverviewButton = null!;
    private GatewayButton navigationDevicesButton = null!;
    private GatewayButton navigationRealtimeButton = null!;
    private GatewayButton navigationHistoryButton = null!;
    private GatewayButton navigationDiagnosticsButton = null!;
    private GatewayButton navigationDatabaseButton = null!;
    private GatewayButton navigationCloudButton = null!;
    private GatewayButton navigationLogsButton = null!;
}
