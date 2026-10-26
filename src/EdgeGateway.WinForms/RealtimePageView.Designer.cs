using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class RealtimePageView
{
    private System.ComponentModel.IContainer? components;
    private TableLayoutPanel contentLayout = null!;
    private Label titleLabel = null!;
    private Label descriptionLabel = null!;
    private FlowLayoutPanel toolbarHost = null!;
    private ComboBox deviceSelector = null!;
    private TextBox pointSearch = null!;
    private DpiDataGridView realtimeGrid = null!;

    internal ComboBox DeviceSelector => deviceSelector;
    internal TextBox PointSearch => pointSearch;
    internal DataGridView RealtimeGrid => realtimeGrid;
    internal Control ToolbarHost => toolbarHost;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        contentLayout = new TableLayoutPanel();
        titleLabel = new Label { Name = "RealtimePageTitle", Text = "实时监控", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 21F, FontStyle.Bold), ForeColor = UiTheme.Ink, Margin = new Padding(0, 0, 0, 2) };
        descriptionLabel = new Label { Name = "RealtimePageDescription", Text = "显示最新工程值、原始值和采样质量；只有配置为可写的点位才能手动写入。", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F), ForeColor = UiTheme.Muted, Margin = new Padding(0, 0, 0, 8) };
        toolbarHost = new FlowLayoutPanel { Name = "RealtimeToolbarHost", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 4) };
        deviceSelector = new ComboBox { Name = "RealtimeDeviceSelector", DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 4, 8, 0), IntegralHeight = false, DropDownHeight = 320 };
        pointSearch = new TextBox { Name = "RealtimePointSearch", PlaceholderText = "搜索点位名称 / 地址", Width = 220, Margin = new Padding(0, 4, 8, 0) };
        realtimeGrid = UiTheme.Grid() as DpiDataGridView ?? new DpiDataGridView();
        realtimeGrid.Name = "RealtimeValues";
        realtimeGrid.Dock = DockStyle.Fill;
        contentLayout.SuspendLayout();
        SuspendLayout();
        Name = "RealtimePageView";
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(20);
        contentLayout.Name = "RealtimeContent";
        contentLayout.Dock = DockStyle.Fill;
        contentLayout.Margin = Padding.Empty;
        contentLayout.ColumnCount = 1;
        contentLayout.RowCount = 4;
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        contentLayout.Controls.Add(titleLabel, 0, 0);
        contentLayout.Controls.Add(descriptionLabel, 0, 1);
        contentLayout.Controls.Add(toolbarHost, 0, 2);
        contentLayout.Controls.Add(realtimeGrid, 0, 3);
        Controls.Add(contentLayout);
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
