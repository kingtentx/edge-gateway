namespace EdgeGateway.WinForms;

public partial class GatewaySwitch
{
    private AntdUI.Switch switchControl = null!;
    private Label captionLabel = null!;

    private void InitializeComponent()
    {
        switchControl = new AntdUI.Switch();
        captionLabel = new Label();
        SuspendLayout();
        switchControl.Name = "SwitchControl";
        switchControl.Fill = UiTheme.Blue;
        switchControl.Size = new Size(52, 30);
        switchControl.WaveSize = 4;
        switchControl.TabStop = false;
        switchControl.AccessibleRole = AccessibleRole.CheckButton;
        captionLabel.Name = "SwitchCaption";
        captionLabel.TextAlign = ContentAlignment.MiddleLeft;
        captionLabel.AutoSize = false;
        captionLabel.UseMnemonic = false;
        captionLabel.TabStop = false;
        Controls.Add(switchControl);
        Controls.Add(captionLabel);
        Size = new Size(180, 32);
        TabStop = true;
        ResumeLayout(false);
    }
}
