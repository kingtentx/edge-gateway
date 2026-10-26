using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class HistoryPageView
{
    private System.ComponentModel.IContainer? components;
    private TableLayoutPanel contentLayout = null!;
    private Label titleLabel = null!;
    private Label descriptionLabel = null!;
    private FlowLayoutPanel toolbarHost = null!;
    private ComboBox groupSelector = null!;
    private ComboBox deviceSelector = null!;
    private ComboBox pointSelector = null!;
    private DateTimePicker fromTime = null!;
    private DateTimePicker toTime = null!;
    private NumericUpDown resultLimit = null!;
    private Label querySummary = null!;
    private TrendChart trendChart = null!;
    private DpiDataGridView historyGrid = null!;

    internal ComboBox GroupSelector => groupSelector;
    internal ComboBox DeviceSelector => deviceSelector;
    internal ComboBox PointSelector => pointSelector;
    internal DateTimePicker FromTime => fromTime;
    internal DateTimePicker ToTime => toTime;
    internal NumericUpDown ResultLimit => resultLimit;
    internal Label QuerySummary => querySummary;
    internal TrendChart TrendChart => trendChart;
    internal DataGridView HistoryGrid => historyGrid;
    internal Control ToolbarHost => toolbarHost;
    internal TableLayoutPanel ContentLayout => contentLayout;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        contentLayout = new TableLayoutPanel();
        titleLabel = new Label { Name = "HistoryPageTitle", Text = "历史与趋势", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 21F, FontStyle.Bold), ForeColor = UiTheme.Ink, Margin = new Padding(0, 0, 0, 2) };
        descriptionLabel = new Label { Name = "HistoryPageDescription", Text = "查询外部数据库的标准点行表；自定义业务表可通过各自的数据库工具查询。", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F), ForeColor = UiTheme.Muted, Margin = new Padding(0, 0, 0, 8) };
        toolbarHost = new FlowLayoutPanel { Name = "HistoryToolbarHost", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 4) };
        groupSelector = CreateChoice("ExternalHistoryGroup", 240);
        deviceSelector = CreateChoice("HistoryDevice", 180);
        pointSelector = CreateChoice("HistoryTag", 220);
        fromTime = new DateTimePicker { Name = "HistoryFrom", Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 190, Value = DateTime.Now.AddHours(-1), Margin = new Padding(0, 4, 8, 0) };
        toTime = new DateTimePicker { Name = "HistoryTo", Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 190, Value = DateTime.Now.AddMinutes(1), Margin = new Padding(0, 4, 8, 0) };
        resultLimit = new NumericUpDown { Name = "HistoryLimit", Minimum = 100, Maximum = 10000, Value = 5000, Increment = 100, Width = 90, Margin = new Padding(0, 4, 8, 0) };
        querySummary = UiTheme.Label("选择时间范围并查询，最多返回 10,000 条。", 9, UiTheme.Muted);
        trendChart = new TrendChart { Name = "HistoryTrend", Dock = DockStyle.Fill, Visible = false };
        historyGrid = UiTheme.Grid() as DpiDataGridView ?? new DpiDataGridView();
        historyGrid.Name = "ExternalHistoryResults";
        historyGrid.Dock = DockStyle.Fill;
        contentLayout.SuspendLayout();
        SuspendLayout();
        Name = "HistoryPageView";
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(20);
        contentLayout.Name = "HistoryContent";
        contentLayout.Dock = DockStyle.Fill;
        contentLayout.Margin = Padding.Empty;
        contentLayout.ColumnCount = 1;
        contentLayout.RowCount = 6;
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        querySummary.Name = "HistoryQuerySummary";
        querySummary.Dock = DockStyle.Fill;
        querySummary.Margin = new Padding(0, 4, 0, 6);
        contentLayout.Controls.Add(titleLabel, 0, 0);
        contentLayout.Controls.Add(descriptionLabel, 0, 1);
        contentLayout.Controls.Add(toolbarHost, 0, 2);
        contentLayout.Controls.Add(querySummary, 0, 3);
        contentLayout.Controls.Add(trendChart, 0, 4);
        contentLayout.Controls.Add(historyGrid, 0, 5);
        Controls.Add(contentLayout);
        contentLayout.ResumeLayout(false);
        contentLayout.PerformLayout();
        ResumeLayout(false);
    }

    private static ComboBox CreateChoice(string name, int width) => new()
    {
        Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Width = width,
        Margin = new Padding(0, 4, 8, 0), IntegralHeight = false, DropDownHeight = 320
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }
}
