namespace EdgeGateway.WinForms;

/// <summary>
/// Design-time fixed structure for device and point maintenance.
/// Fixed columns and actions live in Designer; only rows and command state are bound at runtime.
/// </summary>
internal sealed partial class DevicesPageView : UserControl
{
    private bool _arranging;
    internal TableLayoutPanel ContentLayout => contentLayout;
    internal Control DeviceToolbarHost => deviceToolbarHost;
    internal Control DeviceFiltersHost => deviceFiltersHost;
    internal GatewayTable DeviceGrid => deviceGrid;
    internal AntdUI.Button AddDeviceButton => addDeviceButton;
    internal AntdUI.Button ProbeDeviceButton => probeDeviceButton;
    internal Label DeviceSummary => deviceSummaryLabel;
    public DevicesPageView() { InitializeComponent(); pageViewport.Layout += (_, _) => PerformLayout(); GridPagination.Attach(deviceGrid, deviceGridPagination, deviceGridPaginationTotal, deviceGridPaginationSize); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_arranging || contentLayout is null || !IsHandleCreated
            || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
        _arranging = true;
        try
        {
            var width = Math.Max(1, pageViewport.ScrollClientSize.Width);
            // Measure wrapped rows at their current width before using their
            // heights to size the page; stale narrow-window heights leave gaps.
            contentLayout.Width = width;
            var filterWidth = Math.Max(1, width - deviceFiltersHost.Padding.Horizontal);
            var filterHeight = filters.GetPreferredSize(new Size(filterWidth, 1)).Height;
            deviceFiltersHost.RowStyles[0].SizeType = SizeType.Absolute;
            deviceFiltersHost.RowStyles[0].Height = filterHeight;
            contentLayout.PerformLayout();
            var fixedHeight = titleLabel.Height + titleLabel.Margin.Vertical
                + descriptionLabel.Height + descriptionLabel.Margin.Vertical
                + deviceToolbarHost.Height + deviceFiltersHost.Height + deviceGridPagination.Parent!.Height;
            var minimumHeight = fixedHeight + UiTheme.Scale(this, 220);
            pageViewport.AutoScrollMinSize = new Size(0, minimumHeight);
            contentLayout.SetBounds(pageViewport.AutoScrollPosition.X, pageViewport.AutoScrollPosition.Y,
                width, Math.Max(pageViewport.ScrollClientSize.Height, minimumHeight));
        }
        finally { _arranging = false; }
    }
}
