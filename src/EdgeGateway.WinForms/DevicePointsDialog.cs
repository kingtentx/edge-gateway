namespace EdgeGateway.WinForms;

/// <summary>Fixed point-management surface; application commands are bound by the owner.</summary>
internal sealed partial class DevicePointsDialog : DpiAwareForm
{
    private bool _arranging;
    internal DataGridView TagGrid => tagGrid;
    internal GatewayInput TagSearch => tagSearch;
    internal Label TagHeading => tagHeadingLabel;
    internal Label TagSummary => tagSummaryLabel;
    internal AntdUI.Button AddTagButton => addTagButton;
    internal AntdUI.Button EditTagButton => editTagButton;
    internal AntdUI.Button SinglePointReadButton => singlePointReadButton;
    internal void SetFeedback(string message) => feedbackLabel.Text = message;
    public DevicePointsDialog()
    {
        InitializeComponent();
        ConfigureWindow(new Size(1160, 700), new Size(760, 460));
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_arranging || tagHeaderLayout is null || !IsDpiLayoutReady) return;
        _arranging = true;
        try
        {
            var width = Math.Max(1, pointPane.ClientSize.Width - pointPane.Padding.Horizontal);
            var height = Math.Max(UiTheme.Scale(this, 44), tagToolbarHost.GetPreferredSize(new Size(width, 0)).Height);
            if (tagHeaderLayout.RowStyles[2].Height != height) tagHeaderLayout.RowStyles[2].Height = height;
        }
        finally { _arranging = false; }
    }
}
