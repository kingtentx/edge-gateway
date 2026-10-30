namespace EdgeGateway.WinForms;

internal sealed partial class DeviceParameterDialog
{
    private GatewayInput keyInput = null!, valueInput = null!;
    private void InitializeComponent()
    {
        var tab = new TabPage("参数") { Name = "ParameterFields" };
        var viewport = new AntdUI.Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var keyLabel = new Label { Text = "参数标识", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 3, 0, 5) };
        var valueLabel = new Label { Text = "参数值", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 3, 0, 5) };
        keyInput = new GatewayInput { Dock = DockStyle.Fill };
        valueInput = new GatewayInput { Dock = DockStyle.Fill };
        var keyFrame = new InputFrame { ContentWidth = 400, Height = 38, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 0, 5) };
        var valueFrame = new InputFrame { ContentWidth = 400, Height = 38, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 0, 5) };
        keyFrame.Controls.Add(keyInput); valueFrame.Controls.Add(valueInput);
        fields.Controls.Add(keyLabel, 0, 0); fields.Controls.Add(keyFrame, 1, 0);
        fields.Controls.Add(valueLabel, 0, 1); fields.Controls.Add(valueFrame, 1, 1);
        viewport.Controls.Add(fields); tab.Controls.Add(viewport); Tabs.TabPages.Add(tab);
    }
}
