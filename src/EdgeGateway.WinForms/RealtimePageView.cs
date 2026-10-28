namespace EdgeGateway.WinForms;

/// <summary>Fixed design-time structure for real-time point monitoring.</summary>
internal sealed partial class RealtimePageView : UserControl
{
    internal ComboBox DeviceSelector => deviceSelector;
    internal GatewayInput PointSearch => pointSearch;
    internal DataGridView RealtimeGrid => realtimeGrid;
    internal Control ToolbarHost => toolbarHost;

    public RealtimePageView() { InitializeComponent(); GridPagination.Attach(realtimeGrid, realtimeGridPagination, realtimeGridPaginationTotal, realtimeGridPaginationSize); }
}
