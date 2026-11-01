namespace EdgeGateway.WinForms;

/// <summary>Fixed design-time structure for real-time point monitoring.</summary>
internal sealed partial class RealtimePageView : UserControl
{
    internal ComboBox GroupSelector => groupSelector;
    internal AntdUI.Tree DeviceTree => deviceTree;
    internal GatewayInput PointSearch => pointSearch;
    internal GatewayTable RealtimeGrid => realtimeGrid;
    internal Control ToolbarHost => toolbarHost;
    internal void UpdatePointSummary(int devices, int points)
    {
        var text = $"当前结果：{devices} 台设备，{points} 个点位；分页显示，可在底部翻页或调整每页条数。";
        if (pointSummary.Text != text) pointSummary.Text = text;
    }
    internal AntdUI.Splitter BrowserSplitter => browserSplitter;
    private float _preferredTreeWidth = 220F;
    private bool _arrangingBrowser;
    private bool _movingSplitter;

    public RealtimePageView()
    {
        InitializeComponent();
        GridPagination.Attach(realtimeGrid, realtimeGridPagination, realtimeGridPaginationTotal, realtimeGridPaginationSize);
        browserSplitter.SizeChanged += (_, _) => ArrangeBrowser();
        browserSplitter.SplitterMoving += (_, _) => _movingSplitter = true;
        browserSplitter.SplitterMoved += (_, _) =>
        {
            if (_arrangingBrowser || !_movingSplitter) return;
            _movingSplitter = false;
            _preferredTreeWidth = browserSplitter.SplitterDistance * 96F / DeviceDpi;
        };
        HandleCreated += (_, _) => ArrangeBrowser();
        DpiChangedAfterParent += (_, _) => ArrangeBrowser();
    }

    private void ArrangeBrowser()
    {
        var splitter = (SplitContainer)browserSplitter;
        if (_arrangingBrowser || splitter.ClientSize.Width <= UiTheme.Scale(this, 8)) return;
        _arrangingBrowser = true;
        try
        {
            // Remember the user's logical width, even when a narrow viewport temporarily clamps it.
            splitter.SplitterWidth = UiTheme.Scale(this, 8);
            var available = splitter.ClientSize.Width - splitter.SplitterWidth;
            var leftMinimum = Math.Min(UiTheme.Scale(this, 176), available / 3);
            var rightMinimum = Math.Min(UiTheme.Scale(this, 320), available - leftMinimum);
            browserSplitter.Panel1MinSize = 0;
            browserSplitter.Panel2MinSize = 0;
            browserSplitter.SplitterDistance = Math.Clamp(
                UiTheme.Scale(this, (int)Math.Round(_preferredTreeWidth)), leftMinimum, available - rightMinimum);
            browserSplitter.Panel1MinSize = leftMinimum;
            browserSplitter.Panel2MinSize = rightMinimum;
        }
        finally { _arrangingBrowser = false; }
    }
}
