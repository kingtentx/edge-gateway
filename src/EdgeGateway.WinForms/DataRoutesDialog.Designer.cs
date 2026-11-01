namespace EdgeGateway.WinForms;

internal sealed partial class DataRoutesDialog
{
    private TabPage detailsTab = null!;
    private TableLayoutPanel layout = null!;
    private FlowLayoutPanel actions = null!;
    private GatewayInput details = null!;
    private GatewayButton realtime = null!, history = null!, logs = null!, database = null!, diagnostics = null!;

    private void InitializeComponent()
    {
        detailsTab = new TabPage { Text = "采样与数据去向", BackColor = Color.White };
        layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        realtime = new GatewayButton { Name = "RouteRealtime", Text = "实时监控", AutoSize = true, MinimumSize = new Size(100, 38) };
        history = new GatewayButton { Name = "RouteHistory", Text = "采样历史", AutoSize = true, MinimumSize = new Size(100, 38) };
        logs = new GatewayButton { Name = "RouteLogs", Text = "相关日志", AutoSize = true, MinimumSize = new Size(100, 38) };
        database = new GatewayButton { Name = "RouteDatabase", Text = "配置数据库写入", AutoSize = true, MinimumSize = new Size(150, 38) };
        diagnostics = new GatewayButton { Name = "RouteDiagnostics", Text = "设备诊断", AutoSize = true, MinimumSize = new Size(100, 38) };
        actions.Controls.AddRange([realtime, history, logs, database, diagnostics]);
        details = new GatewayInput { Name = "DataRoutesDetails", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        layout.Controls.Add(actions, 0, 0); layout.Controls.Add(details, 0, 1);
        detailsTab.Controls.Add(layout); Tabs.TabPages.Add(detailsTab);
    }
}
