namespace EdgeGateway.WinForms;

/// <summary>
/// Design-time fixed structure for device and point maintenance.
/// Fixed columns and actions live in Designer; only rows and command state are bound at runtime.
/// </summary>
internal sealed partial class DevicesPageView : UserControl
{
    private bool _arranging;
    private bool _sizingSplit;
    private bool _movingSplit;
    private double _deviceShare = .4;
    internal SplitContainer ListSplit => listSplit;
    internal TableLayoutPanel ContentLayout => contentLayout;
    internal Control DeviceToolbarHost => deviceToolbarHost;
    internal Control DeviceFiltersHost => deviceFiltersHost;
    internal DataGridView DeviceGrid => deviceGrid;
    internal TableLayoutPanel TagHeaderLayout => tagHeaderLayout;
    internal Control TagToolbarHost => tagToolbarHost;
    internal GatewayInput TagSearch => tagSearch;
    internal AntdUI.Button AddDeviceButton => addDeviceButton;
    internal AntdUI.Button ProbeDeviceButton => probeDeviceButton;
    internal AntdUI.Button AddTagButton => addTagButton;
    internal AntdUI.Button EditTagButton => editTagButton;
    internal AntdUI.Button SinglePointReadButton => singlePointReadButton;
    internal DataGridView TagGrid => tagGrid;
    internal Label DeviceSummary => deviceSummaryLabel;
    internal Label TagHeading => tagHeadingLabel;
    internal Label TagSummary => tagSummaryLabel;
    public DevicesPageView()
    {
        InitializeComponent();
        listSplit.SplitterMoving += (_, _) => _movingSplit = true;
        listSplit.SplitterMoved += (_, _) =>
        {
            if (!_movingSplit || _sizingSplit || !IsHandleCreated || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
            _movingSplit = false;
            _deviceShare = (double)listSplit.SplitterDistance / Math.Max(1, listSplit.ClientSize.Height - listSplit.SplitterWidth);
        };
        listSplit.SizeChanged += (_, _) => ArrangeSplit();
        listSplit.DoubleClick += (_, _) => { _deviceShare = .4; ArrangeSplit(); };
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_arranging || tagToolbarHost is null || !IsHandleCreated
            || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
        _arranging = true;
        try
        {
            var contentWidth = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            var fixedHeight = titleLabel.Height + titleLabel.Margin.Vertical
                + descriptionLabel.Height + descriptionLabel.Margin.Vertical
                + deviceToolbarHost.Height + deviceFiltersHost.Height;
            var minimumHeight = Math.Max(UiTheme.Scale(this, 620) - Padding.Vertical,
                fixedHeight + tagHeaderLayout.Height + UiTheme.Scale(this, 240));
            var contentHeight = Math.Max(ClientSize.Height - Padding.Vertical, minimumHeight);
            AutoScrollMinSize = new Size(0, minimumHeight + Padding.Vertical);
            contentLayout.SetBounds(Padding.Left + AutoScrollPosition.X, Padding.Top + AutoScrollPosition.Y,
                contentWidth, contentHeight);
            // A nested auto-sized table can measure a wrapping toolbar at a very
            // narrow provisional width and reserve several empty rows. Measure
            // against the real available width before distributing grid space.
            var width = Math.Max(1, contentLayout.ClientSize.Width - tagHeaderLayout.Padding.Horizontal);
            var height = tagToolbarHost.GetPreferredSize(new Size(width, 0)).Height;
            height = Math.Max(UiTheme.Scale(this, 44), height);
            if (tagHeaderLayout.RowStyles[2].Height != height)
                tagHeaderLayout.RowStyles[2].Height = height;
            ArrangeSplit();
        }
        finally { _arranging = false; }
    }

    private void ArrangeSplit()
    {
        if (_sizingSplit || !IsHandleCreated || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
        _sizingSplit = true;
        try
        {
            var available = listSplit.ClientSize.Height - listSplit.SplitterWidth;
            var topMinimum = UiTheme.Scale(this, 110);
            var bottomMinimum = tagHeaderLayout.Height + UiTheme.Scale(this, 110);
            if (available < topMinimum + bottomMinimum) return;
            // Update minima after DPI/layout has settled, never during design-time scaling.
            listSplit.Panel1MinSize = topMinimum;
            listSplit.Panel2MinSize = bottomMinimum;
            listSplit.SplitterDistance = Math.Clamp((int)Math.Round(available * _deviceShare), topMinimum, available - bottomMinimum);
        }
        finally { _sizingSplit = false; }
    }

}
