namespace EdgeGateway.WinForms;

/// <summary>Fixed design-time layout for communication diagnostics.</summary>
internal sealed partial class DiagnosticsPageView : UserControl
{
    internal DataGridView DiagnosticsGrid => diagnosticsGrid;
    internal Control ToolbarHost => toolbarHost;

    public DiagnosticsPageView() => InitializeComponent();
}
