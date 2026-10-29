namespace EdgeGateway.WinForms;

internal sealed partial class ServerDiskBar
{
    private TableLayoutPanel diskLayout = null!;
    private Label diskName = null!, diskDetails = null!;
    private AntdUI.Progress diskProgress = null!;
    private void InitializeComponent()
    {
        diskLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        diskLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        diskLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        diskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        diskLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        diskName = new Label { Name = "DiskName", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), TextAlign = ContentAlignment.MiddleLeft, TextMultiLine = false, AutoSizeMode = AntdUI.TAutoSize.Width };
        diskProgress = new AntdUI.Progress { Name = "DiskCapacity", Dock = DockStyle.Fill, Margin = Padding.Empty, UseTextCenter = true, ShowTextDot = 1, ValueRatio = 1.2F };
        diskDetails = new Label { Name = "DiskDetails", Dock = DockStyle.Fill, TextMultiLine = true, ForeColor = UiTheme.Muted, Margin = Padding.Empty };
        diskLayout.Controls.Add(diskName, 0, 0);
        diskLayout.Controls.Add(diskProgress, 1, 0);
        diskLayout.Controls.Add(diskDetails, 0, 1); diskLayout.SetColumnSpan(diskDetails, 2);
        Radius = 6; Padding = new Padding(8, 6, 8, 6); Margin = Padding.Empty;
        Back = Color.White; Controls.Add(diskLayout);
    }
}
