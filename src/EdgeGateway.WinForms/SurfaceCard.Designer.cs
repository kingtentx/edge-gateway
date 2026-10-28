namespace EdgeGateway.WinForms;

internal sealed partial class SurfaceCard
{
    private TableLayoutPanel content = null!;
    private void InitializeComponent()
    {
        content = new TableLayoutPanel();
        content.Name = "CardContent";
        content.Dock = DockStyle.Fill;
        content.AutoSize = true;
        content.Margin = Padding.Empty;
        content.BackColor = Color.Transparent;
        Radius = 6;
        Back = Color.White;
        base.Controls.Add(content);
    }
}
