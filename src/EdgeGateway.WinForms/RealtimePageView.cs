namespace EdgeGateway.WinForms;

/// <summary>Fixed design-time structure for real-time point monitoring.</summary>
internal sealed partial class RealtimePageView : UserControl
{
    internal ComboBox GroupSelector => groupSelector;
    internal AntdUI.Tree DeviceTree => deviceTree;
    internal GatewayInput PointSearch => pointSearch;
    internal GatewayTable RealtimeGrid => realtimeGrid;
    internal Control ToolbarHost => toolbarHost;

    public RealtimePageView()
    {
        InitializeComponent();
        GridPagination.Attach(realtimeGrid, realtimeGridPagination, realtimeGridPaginationTotal, realtimeGridPaginationSize);
        SizeChanged += (_, _) => ArrangeBrowser();
        DpiChangedAfterParent += (_, _) => ArrangeBrowser();
    }

    private void ArrangeBrowser()
    {
        var logicalWidth = ClientSize.Width * 96F / DeviceDpi;
        bodyLayout.ColumnStyles[0].Width = UiTheme.Scale(this, (int)Math.Clamp(logicalWidth * .26F, 176F, 240F));
    }
}
