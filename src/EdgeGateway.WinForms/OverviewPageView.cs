namespace EdgeGateway.WinForms;

/// <summary>Fixed overview header and host; metrics/charts remain data-driven dashboard widgets.</summary>
internal sealed partial class OverviewPageView : UserControl
{
    internal Control DashboardHost => dashboardHost;

    public OverviewPageView() => InitializeComponent();
}
