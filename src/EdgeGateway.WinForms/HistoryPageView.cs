namespace EdgeGateway.WinForms;

/// <summary>Fixed design-time structure for log and external history query and trends.</summary>
internal sealed partial class HistoryPageView : UserControl
{
    internal ComboBox SourceSelector => sourceSelector;
    internal NumericUpDown ScanLimit => scanLimit;
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

    public HistoryPageView() { InitializeComponent(); GridPagination.Attach(historyGrid, historyGridPagination, historyGridPaginationTotal, historyGridPaginationSize); }
}
