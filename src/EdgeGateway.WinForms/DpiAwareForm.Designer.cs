namespace EdgeGateway.WinForms;

public partial class DpiAwareForm
{
    private AntdUI.PageHeader windowHeader = null!;
    private void InitializeWindowComponents()
    {
        windowHeader = new AntdUI.PageHeader();
        windowHeader.Name = "WindowHeader";
        windowHeader.Dock = DockStyle.Top;
        windowHeader.Height = 38;
        windowHeader.ShowButton = true;
        windowHeader.Text = Text;
        windowHeader.Padding = new Padding(12, 0, 0, 0);
        Controls.Add(windowHeader);
    }
}
