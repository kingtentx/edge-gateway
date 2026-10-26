using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class DiagnosticsPageView
{
    private System.ComponentModel.IContainer? components;
    private TableLayoutPanel contentLayout = null!;
    private Label titleLabel = null!;
    private Label descriptionLabel = null!;
    private FlowLayoutPanel toolbarHost = null!;
    private DpiDataGridView diagnosticsGrid = null!;

    internal DataGridView DiagnosticsGrid => diagnosticsGrid;
    internal Control ToolbarHost => toolbarHost;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        contentLayout = new TableLayoutPanel();
        titleLabel = new Label { Name = "DiagnosticsPageTitle", Text = "通信诊断", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 21F, FontStyle.Bold), ForeColor = UiTheme.Ink, Margin = new Padding(0, 0, 0, 2) };
        descriptionLabel = new Label { Name = "DiagnosticsPageDescription", Text = "检查采集与上报链路。发送确认按平台协议判断，最终业务入库仍需在平台核对。", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F), ForeColor = UiTheme.Muted, Margin = new Padding(0, 0, 0, 8) };
        toolbarHost = new FlowLayoutPanel { Name = "DiagnosticsToolbarHost", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 4) };
        diagnosticsGrid = UiTheme.Grid() as DpiDataGridView ?? new DpiDataGridView();
        diagnosticsGrid.Name = "TelemetryDiagnostics";
        diagnosticsGrid.Dock = DockStyle.Fill;
        contentLayout.SuspendLayout();
        SuspendLayout();
        Name = "DiagnosticsPageView";
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(20);
        contentLayout.Name = "DiagnosticsContent";
        contentLayout.Dock = DockStyle.Fill;
        contentLayout.ColumnCount = 1;
        contentLayout.RowCount = 4;
        contentLayout.Margin = Padding.Empty;
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        contentLayout.Controls.Add(titleLabel, 0, 0);
        contentLayout.Controls.Add(descriptionLabel, 0, 1);
        contentLayout.Controls.Add(toolbarHost, 0, 2);
        contentLayout.Controls.Add(diagnosticsGrid, 0, 3);
        Controls.Add(contentLayout);
        contentLayout.ResumeLayout(false);
        contentLayout.PerformLayout();
        ResumeLayout(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }
}
