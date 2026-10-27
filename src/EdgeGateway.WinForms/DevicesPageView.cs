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
    internal AntdUI.Button AddDeviceButton => addDeviceButton;
    internal AntdUI.Button ProbeDeviceButton => probeDeviceButton;
    internal Label DeviceSummary => deviceSummaryLabel;
    public DevicesPageView() => InitializeComponent();

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_arranging || contentLayout is null || !IsHandleCreated
            || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
        _arranging = true;
        try
        {
            var width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            var fixedHeight = titleLabel.Height + titleLabel.Margin.Vertical
                + descriptionLabel.Height + descriptionLabel.Margin.Vertical
                + deviceToolbarHost.Height + deviceFiltersHost.Height;
            var minimumHeight = fixedHeight + UiTheme.Scale(this, 220);
            AutoScrollMinSize = new Size(0, minimumHeight + Padding.Vertical);
            contentLayout.SetBounds(Padding.Left + AutoScrollPosition.X, Padding.Top + AutoScrollPosition.Y,
                width, Math.Max(ClientSize.Height - Padding.Vertical, minimumHeight));
        }
        finally { _arranging = false; }
    }
}
