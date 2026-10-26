using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class DatabasePageView
{
    private System.ComponentModel.IContainer? components;
    private TableLayoutPanel contentLayout = null!;
    private Label titleLabel = null!;
    private Label descriptionLabel = null!;
    private Label databaseStateLabel = null!;
    private TabControl databaseTabs = null!;
    private TabPage connectionsTab = null!;
    private TabPage writeGroupsTab = null!;
    private FlowLayoutPanel connectionsToolbarHost = null!;
    private FlowLayoutPanel groupsToolbarHost = null!;
    private DpiDataGridView connectionsGrid = null!;
    private DpiDataGridView groupsGrid = null!;

    internal TabControl DatabaseTabs => databaseTabs;
    internal DataGridView ConnectionsGrid => connectionsGrid;
    internal DataGridView GroupsGrid => groupsGrid;
    internal Label DatabaseState => databaseStateLabel;
    internal Control ConnectionsToolbarHost => connectionsToolbarHost;
    internal Control GroupsToolbarHost => groupsToolbarHost;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        contentLayout = new TableLayoutPanel();
        titleLabel = new Label { Name = "DatabasePageTitle", Text = "数据库", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 21F, FontStyle.Bold), ForeColor = UiTheme.Ink, Margin = new Padding(0, 0, 0, 2) };
        descriptionLabel = new Label { Name = "DatabasePageDescription", Text = "配置 MySQL、PostgreSQL 或 Oracle；标准点行可直接查询历史，自定义映射写入已有业务表。", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F), ForeColor = UiTheme.Muted, Margin = new Padding(0, 0, 0, 8) };
        databaseStateLabel = UiTheme.Label("默认采集结果写入日志。需要长期查询时，添加外部数据库并配置写入组。", 10, UiTheme.Muted);
        databaseTabs = new EditorLayout.EditorTabs { Name = "DatabaseTabs", Dock = DockStyle.Fill, Multiline = true, Margin = Padding.Empty };
        connectionsTab = new TabPage("数据库连接") { Name = "DatabaseConnectionsTab", BackColor = Color.White };
        writeGroupsTab = new TabPage("采集写入组") { Name = "DatabaseWriteGroupsTab", BackColor = Color.White };
        connectionsToolbarHost = new FlowLayoutPanel { Name = "DatabaseConnectionsToolbarHost", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 4) };
        groupsToolbarHost = new FlowLayoutPanel { Name = "DatabaseWriteGroupsToolbarHost", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 4) };
        connectionsGrid = UiTheme.Grid() as DpiDataGridView ?? new DpiDataGridView();
        groupsGrid = UiTheme.Grid() as DpiDataGridView ?? new DpiDataGridView();
        connectionsGrid.Name = "DatabaseConnections";
        groupsGrid.Name = "DatabaseWriteGroups";
        connectionsGrid.Dock = groupsGrid.Dock = DockStyle.Fill;

        contentLayout.SuspendLayout();
        connectionsTab.SuspendLayout();
        writeGroupsTab.SuspendLayout();
        SuspendLayout();
        Name = "DatabasePageView";
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(20);
        contentLayout.Name = "DatabaseContent";
        contentLayout.Dock = DockStyle.Fill;
        contentLayout.ColumnCount = 1;
        contentLayout.RowCount = 3;
        contentLayout.Margin = Padding.Empty;
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        databaseStateLabel.Name = "DatabaseConfigurationState";
        databaseStateLabel.Dock = DockStyle.Fill;
        databaseStateLabel.Margin = new Padding(8, 4, 8, 8);
        connectionsTab.Controls.Add(connectionsGrid);
        writeGroupsTab.Controls.Add(groupsGrid);
        databaseTabs.TabPages.Add(connectionsTab);
        databaseTabs.TabPages.Add(writeGroupsTab);
        contentLayout.Controls.Add(titleLabel, 0, 0);
        contentLayout.Controls.Add(descriptionLabel, 0, 1);
        contentLayout.Controls.Add(databaseStateLabel, 0, 2);
        contentLayout.Controls.Add(databaseTabs, 0, 3);
        Controls.Add(contentLayout);
        connectionsTab.ResumeLayout(false);
        writeGroupsTab.ResumeLayout(false);
        contentLayout.ResumeLayout(false);
        contentLayout.PerformLayout();
        ResumeLayout(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }
}
