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
        // A transparent WinForms child asks the AntdUI parent to redraw its full
        // rounded surface on each resize/erase. The inset content is already white.
        content.BackColor = Color.White;
        Radius = 6;
        Back = Color.White;
        base.Controls.Add(content);
    }
}
