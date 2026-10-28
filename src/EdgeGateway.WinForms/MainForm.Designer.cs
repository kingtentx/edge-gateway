#nullable enable
using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private TableLayoutPanel navigationHeaderPanel = null!;
    private AntdUI.Avatar navigationApplicationIcon = null!;
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

    private GatewayNavigationMenu navigationMenu = null!;
    private GatewayButton collapseNavigationButton = null!;
    private AntdUI.MenuItem navigationOverviewItem = null!;
    private AntdUI.MenuItem navigationDevicesItem = null!;
    private AntdUI.MenuItem navigationRealtimeItem = null!;
    private AntdUI.MenuItem navigationHistoryItem = null!;
    private AntdUI.MenuItem navigationDiagnosticsItem = null!;
    private AntdUI.MenuItem navigationDatabaseItem = null!;
    private AntdUI.MenuItem navigationCloudItem = null!;
    private AntdUI.MenuItem navigationLogsItem = null!;

    private void InitializeComponent()
    {
        navigationHeaderPanel = new TableLayoutPanel();
        navigationApplicationIcon = new AntdUI.Avatar();
        applicationShell = new TableLayoutPanel();
        navigationViewportPanel = new Panel();
        navigationContentPanel = new TableLayoutPanel();
        navigationBrandPanel = new FlowLayoutPanel();
        brandTitleLabel = new Label();
        brandCaptionLabel = new Label();
        navigationMenu = new GatewayNavigationMenu();
        collapseNavigationButton = new GatewayButton();
        navigationOverviewItem = new AntdUI.MenuItem();
        navigationOverviewItem.Name = "overview";
        navigationOverviewItem.Text = "运行总览";
        navigationOverviewItem.IconSvg = "DashboardOutlined";
        navigationMenu.Items.Add(navigationOverviewItem);
        navigationDevicesItem = new AntdUI.MenuItem();
        navigationDevicesItem.Name = "devices";
        navigationDevicesItem.Text = "设备与点位";
        navigationDevicesItem.IconSvg = "ApartmentOutlined";
        navigationMenu.Items.Add(navigationDevicesItem);
        navigationRealtimeItem = new AntdUI.MenuItem();
        navigationRealtimeItem.Name = "realtime";
        navigationRealtimeItem.Text = "实时监控";
        navigationRealtimeItem.IconSvg = "FundOutlined";
        navigationMenu.Items.Add(navigationRealtimeItem);
        navigationHistoryItem = new AntdUI.MenuItem();
        navigationHistoryItem.Name = "history";
        navigationHistoryItem.Text = "历史与趋势";
        navigationHistoryItem.IconSvg = "LineChartOutlined";
        navigationMenu.Items.Add(navigationHistoryItem);
        navigationDiagnosticsItem = new AntdUI.MenuItem();
        navigationDiagnosticsItem.Name = "diagnostics";
        navigationDiagnosticsItem.Text = "通信诊断";
        navigationDiagnosticsItem.IconSvg = "ApiOutlined";
        navigationMenu.Items.Add(navigationDiagnosticsItem);
        navigationDatabaseItem = new AntdUI.MenuItem();
        navigationDatabaseItem.Name = "database";
        navigationDatabaseItem.Text = "数据库";
        navigationDatabaseItem.IconSvg = "DatabaseOutlined";
        navigationMenu.Items.Add(navigationDatabaseItem);
        navigationCloudItem = new AntdUI.MenuItem();
        navigationCloudItem.Name = "cloud";
        navigationCloudItem.Text = "平台与系统";
        navigationCloudItem.IconSvg = "CloudOutlined";
        navigationMenu.Items.Add(navigationCloudItem);
        navigationLogsItem = new AntdUI.MenuItem();
        navigationLogsItem.Name = "logs";
        navigationLogsItem.Text = "运行日志";
        navigationLogsItem.IconSvg = "FileTextOutlined";
        navigationMenu.Items.Add(navigationLogsItem);
        navigationMenu.Name = "NavigationMenu";
        navigationMenu.Dock = DockStyle.Fill;
        navigationMenu.Margin = Padding.Empty;
        navigationMenu.Padding = new Padding(4);
        navigationMenu.Radius = 6;
        navigationMenu.Gap = 10;
        navigationMenu.itemMargin = 6;
        navigationMenu.IconRatio = 1F;
        navigationMenu.BackColor = Color.White;
        navigationMenu.ForeColor = UiTheme.Ink;
        navigationMenu.BackActive = UiTheme.BlueSoft;
        navigationMenu.ForeActive = UiTheme.Blue;
        navigationMenu.Unique = true;
        navigationMenu.AutoCollapse = false;
        navigationMenu.TabStop = true;
        collapseNavigationButton.Name = "CollapseNavigation";
        collapseNavigationButton.AccessibleName = "收起导航菜单";
        collapseNavigationButton.IconSvg = "MenuOutlined";
        collapseNavigationButton.Text = "";
        collapseNavigationButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        collapseNavigationButton.Width = 56;
        collapseNavigationButton.Height = 40;
        collapseNavigationButton.Margin = Padding.Empty;
        collapseNavigationButton.WaveSize = 0;
        navigationContentPanel.Controls.Add(navigationMenu, 0, 2);
        navigationContentPanel.Controls.Add(collapseNavigationButton, 0, 1);
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
        applicationShell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184F));
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
        navigationContentPanel.AutoSize = false;
        navigationContentPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        navigationContentPanel.BackColor = Color.White;
        navigationContentPanel.ColumnCount = 1;
        navigationContentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        navigationHeaderPanel.Name = "NavigationHeader";
        navigationHeaderPanel.Dock = DockStyle.Fill;
        navigationHeaderPanel.Margin = Padding.Empty;
        navigationHeaderPanel.ColumnCount = 2;
        navigationHeaderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56F));
        navigationHeaderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        navigationHeaderPanel.RowCount = 1;
        navigationHeaderPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        navigationApplicationIcon.Name = "NavigationApplicationIcon";
        navigationApplicationIcon.AccessibleName = "Edge Gateway 应用图标";
        navigationApplicationIcon.ImageFit = AntdUI.TFit.Contain;
        navigationApplicationIcon.Dock = DockStyle.Fill;
        navigationApplicationIcon.Margin = new Padding(14);
        navigationApplicationIcon.TabStop = false;
        navigationHeaderPanel.Controls.Add(navigationApplicationIcon, 0, 0);
        navigationHeaderPanel.Controls.Add(navigationBrandPanel, 1, 0);
        navigationContentPanel.Controls.Add(navigationHeaderPanel, 0, 0);
        navigationContentPanel.Dock = DockStyle.Fill;
        navigationContentPanel.Location = new Point(0, 0);
        navigationContentPanel.Margin = new Padding(0);
        navigationContentPanel.Name = "navigationContentPanel";
        navigationContentPanel.Padding = new Padding(0, 8, 0, 4);
        navigationContentPanel.RowCount = 3;
        navigationContentPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
        navigationContentPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
        navigationContentPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
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
        navigationBrandPanel.Margin = new Padding(0, 4, 0, 0);
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
        brandTitleLabel.Text = "EDGE";
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
        brandCaptionLabel.Text = "边缘采集网关";
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

}
