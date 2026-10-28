namespace EdgeGateway.WinForms;

internal sealed partial class ServerDiskBar
{
    private TableLayoutPanel diskLayout = null!;
    private Label diskName = null!, diskValue = null!, diskDetails = null!;
    private AntdUI.Progress diskProgress = null!;
    private void InitializeComponent()
    {
        diskLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        diskLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        diskLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        diskLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        diskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        diskLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        diskName = new Label { Name = "DiskName", AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        diskValue = new Label { Name = "DiskUsed", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopRight, Margin = Padding.Empty };
        diskProgress = new AntdUI.Progress { Name = "DiskCapacity", Dock = DockStyle.Fill, Margin = Padding.Empty, UseSystemText = false };
        diskDetails = new Label { Name = "DiskDetails", Dock = DockStyle.Fill, TextMultiLine = true, ForeColor = UiTheme.Muted, Margin = Padding.Empty };
        diskLayout.Controls.Add(diskName, 0, 0); diskLayout.Controls.Add(diskValue, 1, 0);
        diskLayout.Controls.Add(diskProgress, 0, 1); diskLayout.SetColumnSpan(diskProgress, 2);
        diskLayout.Controls.Add(diskDetails, 0, 2); diskLayout.SetColumnSpan(diskDetails, 2);
        Radius = 6; Padding = new Padding(12, 10, 12, 10); Margin = Padding.Empty;
        Back = Color.White; Controls.Add(diskLayout);
    }
}
