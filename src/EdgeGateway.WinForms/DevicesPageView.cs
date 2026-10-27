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
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_arranging || tagToolbarHost is null || !IsHandleCreated
            || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
        _arranging = true;
        try
        {
            // A nested auto-sized table can measure a wrapping toolbar at a very
            // narrow provisional width and reserve several empty rows. Measure
            // against the real available width before distributing grid space.
            var width = Math.Max(1, contentLayout.ClientSize.Width - tagHeaderLayout.Padding.Horizontal);
            var height = tagToolbarHost.GetPreferredSize(new Size(width, 0)).Height;
            height = Math.Max(UiTheme.Scale(this, 44), height);
            if (tagHeaderLayout.RowStyles[2].Height != height)
                tagHeaderLayout.RowStyles[2].Height = height;
        }
        finally { _arranging = false; }
    }

}
