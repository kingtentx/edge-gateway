namespace EdgeGateway.WinForms;

internal sealed partial class LogArchivePageView : UserControl
{

    public LogArchivePageView() { InitializeComponent(); GridPagination.Attach(Grid, archivePagination); }

    internal void ConfigureCategory(bool samples)
    {
        DeviceFilter.Visible = TagFilter.Visible = QualityFilter.Visible = samples;
        LevelFilter.Visible = !samples;
        Grid.Columns["Level"].Visible = Grid.Columns["Message"].Visible = !samples;
        foreach (var key in new[] { "Device", "Tag", "Address", "RawValue", "Value", "Quality", "Unit", "Error" })
            Grid.Columns[key].Visible = samples;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (contentLayout is null || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
        State.MaximumSize = new Size(Math.Max(1, contentLayout.ClientSize.Width - 32), 0);
        contentLayout.MinimumSize = new Size(0, filterBar.Height + actions.Height + State.Height + UiTheme.Scale(this, 200));
    }
}
