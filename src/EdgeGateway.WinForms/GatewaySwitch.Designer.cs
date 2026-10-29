namespace EdgeGateway.WinForms;

public partial class GatewaySwitch
{
    private void InitializeComponent()
    {
        Name = "SwitchControl";
        Size = new Size(52, 30);
        MaximumSize = new Size(52, 0);
        WaveSize = 4;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }
}
