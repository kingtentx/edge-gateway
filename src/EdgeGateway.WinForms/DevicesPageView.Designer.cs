using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class DevicesPageView
{
    private System.ComponentModel.IContainer? components;
    private TableLayoutPanel contentLayout = null!;
    private Label titleLabel = null!;
    private Label descriptionLabel = null!;
    private FlowLayoutPanel deviceToolbarHost = null!;
    private Panel deviceFiltersHost = null!;
    private DpiDataGridView deviceGrid = null!;
    private TableLayoutPanel tagHeaderLayout = null!;
    private Label tagHeadingLabel = null!;
    private Label tagSummaryLabel = null!;
    private FlowLayoutPanel tagToolbarHost = null!;
    private TextBox tagSearch = null!;
    private DpiDataGridView tagGrid = null!;

    internal TableLayoutPanel ContentLayout => contentLayout;
    internal Control DeviceToolbarHost => deviceToolbarHost;
    internal Control DeviceFiltersHost => deviceFiltersHost;
    internal DataGridView DeviceGrid => deviceGrid;
    internal TableLayoutPanel TagHeaderLayout => tagHeaderLayout;
    internal Control TagToolbarHost => tagToolbarHost;
    internal TextBox TagSearch => tagSearch;
    internal DataGridView TagGrid => tagGrid;
    internal Label DeviceSummary => deviceSummaryLabel;
    internal Label TagHeading => tagHeadingLabel;
    internal Label TagSummary => tagSummaryLabel;

    private Label deviceSummaryLabel = null!;

    public DevicesPageView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        contentLayout = new TableLayoutPanel();
        titleLabel = new Label();
        descriptionLabel = new Label();
        deviceToolbarHost = new FlowLayoutPanel();
        deviceFiltersHost = new Panel();
        deviceGrid = UiTheme.Grid() as DpiDataGridView ?? new DpiDataGridView();
        tagHeaderLayout = new TableLayoutPanel();
        tagHeadingLabel = new Label();
        tagSummaryLabel = new Label();
        tagToolbarHost = new FlowLayoutPanel();
        tagSearch = new TextBox { Name = "TagSearch", Width = 205, PlaceholderText = "搜索当前设备的点位 / 地址" };
        tagGrid = UiTheme.Grid() as DpiDataGridView ?? new DpiDataGridView();
        deviceSummaryLabel = UiTheme.Label("", 10, UiTheme.Muted);

        contentLayout.SuspendLayout();
        tagHeaderLayout.SuspendLayout();
        SuspendLayout();

        Name = "DevicesPageView";
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(20);
        contentLayout.Name = "DevicesContent";
        contentLayout.Dock = DockStyle.Fill;
        contentLayout.Margin = Padding.Empty;
        contentLayout.Padding = Padding.Empty;
        contentLayout.ColumnCount = 1;
        contentLayout.RowCount = 7;
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 43F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 57F));

        titleLabel.Name = "DevicesPageTitle";
        titleLabel.Text = "设备与点位";
        titleLabel.AutoSize = true;
        titleLabel.Dock = DockStyle.Fill;
        titleLabel.Font = new Font("Microsoft YaHei UI", 21F, FontStyle.Bold);
        titleLabel.ForeColor = UiTheme.Ink;
        titleLabel.Margin = new Padding(0, 0, 0, 2);
        descriptionLabel.Name = "DevicesPageDescription";
        descriptionLabel.Text = "选择设备维护点位；每台设备独立启停，双击设备或点击配置进入同一窗口。";
        descriptionLabel.AutoSize = true;
        descriptionLabel.Dock = DockStyle.Fill;
        descriptionLabel.Font = new Font("Microsoft YaHei UI", 10F);
        descriptionLabel.ForeColor = UiTheme.Muted;
        descriptionLabel.Margin = new Padding(0, 0, 0, 8);

        deviceToolbarHost.Name = "DeviceToolbarHost";
        deviceToolbarHost.Dock = DockStyle.Fill;
        deviceToolbarHost.AutoSize = true;
        deviceToolbarHost.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        deviceToolbarHost.WrapContents = true;
        deviceToolbarHost.FlowDirection = FlowDirection.LeftToRight;
        deviceToolbarHost.Margin = Padding.Empty;
        deviceToolbarHost.Padding = new Padding(0, 4, 0, 2);

        deviceFiltersHost.Name = "DeviceFiltersHost";
        deviceFiltersHost.Dock = DockStyle.Fill;
        deviceFiltersHost.AutoSize = true;
        deviceFiltersHost.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        deviceFiltersHost.Margin = Padding.Empty;
        deviceFiltersHost.Padding = Padding.Empty;

        deviceGrid.Name = "DeviceList";
        deviceGrid.Dock = DockStyle.Fill;
        deviceGrid.Margin = new Padding(0, 0, 0, 8);
        tagHeaderLayout.Name = "TagActions";
        tagHeaderLayout.Dock = DockStyle.Fill;
        tagHeaderLayout.AutoSize = true;
        tagHeaderLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        tagHeaderLayout.ColumnCount = 1;
        tagHeaderLayout.RowCount = 3;
        tagHeaderLayout.Margin = Padding.Empty;
        tagHeaderLayout.Padding = new Padding(0, 8, 0, 6);
        tagHeaderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        tagHeaderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tagHeaderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tagHeaderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        tagHeadingLabel.Name = "TagHeading";
        tagHeadingLabel.Text = "请选择设备";
        tagHeadingLabel.Dock = DockStyle.Fill;
        tagHeadingLabel.AutoSize = true;
        tagHeadingLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        tagHeadingLabel.ForeColor = UiTheme.Ink;
        tagHeadingLabel.Margin = new Padding(0, 0, 0, 1);
        tagSummaryLabel.Name = "TagSummary";
        tagSummaryLabel.Text = "选择设备后查看和维护点位";
        tagSummaryLabel.Dock = DockStyle.Fill;
        tagSummaryLabel.AutoSize = true;
        tagSummaryLabel.Font = new Font("Microsoft YaHei UI", 10F);
        tagSummaryLabel.ForeColor = UiTheme.Muted;
        tagSummaryLabel.Margin = new Padding(0, 0, 0, 3);

        tagToolbarHost.Name = "TagToolbarHost";
        tagToolbarHost.Dock = DockStyle.Fill;
        tagToolbarHost.AutoSize = true;
        tagToolbarHost.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        tagToolbarHost.WrapContents = true;
        tagToolbarHost.FlowDirection = FlowDirection.LeftToRight;
        tagToolbarHost.Margin = Padding.Empty;
        tagToolbarHost.Padding = Padding.Empty;

        tagGrid.Name = "TagList";
        tagGrid.Dock = DockStyle.Fill;
        tagGrid.Margin = Padding.Empty;

        contentLayout.Controls.Add(titleLabel, 0, 0);
        contentLayout.Controls.Add(descriptionLabel, 0, 1);
        contentLayout.Controls.Add(deviceToolbarHost, 0, 2);
        contentLayout.Controls.Add(deviceFiltersHost, 0, 3);
        contentLayout.Controls.Add(deviceGrid, 0, 4);
        contentLayout.Controls.Add(tagHeaderLayout, 0, 5);
        contentLayout.Controls.Add(tagGrid, 0, 6);
        tagHeaderLayout.Controls.Add(tagHeadingLabel, 0, 0);
        tagHeaderLayout.Controls.Add(tagSummaryLabel, 0, 1);
        tagHeaderLayout.Controls.Add(tagToolbarHost, 0, 2);
        Controls.Add(contentLayout);

        tagHeaderLayout.ResumeLayout(false);
        tagHeaderLayout.PerformLayout();
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
