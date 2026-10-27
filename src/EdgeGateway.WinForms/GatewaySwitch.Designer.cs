namespace EdgeGateway.WinForms;

public partial class GatewaySwitch
{
    private AntdUI.Switch switchControl = null!;

    private void InitializeComponent()
    {
        switchControl = new AntdUI.Switch();
        SuspendLayout();
        switchControl.Name = "SwitchControl";
        switchControl.Fill = UiTheme.Blue;
        switchControl.Size = new Size(52, 30);
        switchControl.WaveSize = 4;
        switchControl.TabStop = false;
        switchControl.AccessibleRole = AccessibleRole.CheckButton;
        Controls.Add(switchControl);
        Size = new Size(52, 32);
        TabStop = true;
        ResumeLayout(false);
    }
}
