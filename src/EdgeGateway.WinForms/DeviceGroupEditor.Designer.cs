namespace EdgeGateway.WinForms;

internal sealed partial class DeviceGroupEditor
{
    private AntdUI.Panel groupViewport = null!;
    private TabPage groupTab = null!;
    private TableLayoutPanel fields = null!;
    private Label nameLabel = null!;
    private InputFrame nameSurface = null!;
    internal GatewayInput nameInput = null!;

    private void InitializeComponent()
    {
        groupTab = new TabPage();
        fields = new TableLayoutPanel();
        nameLabel = new Label();
        nameSurface = new InputFrame();
        nameInput = new GatewayInput();
        groupTab.Text = "分组信息";
        groupTab.BackColor = Color.White;
        groupViewport = new AntdUI.Panel { Dock = DockStyle.Fill, AutoScroll = true };
        groupTab.Controls.Add(groupViewport);
        fields.Dock = DockStyle.Top;
        fields.AutoSize = true;
        fields.Padding = new Padding(20);
        fields.ColumnCount = 2;
        fields.RowCount = 1;
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nameLabel.Text = "分组名称";
        nameLabel.AutoSize = true;
        nameLabel.Dock = DockStyle.Fill;
        nameLabel.TextAlign = ContentAlignment.MiddleLeft;
        nameSurface.Dock = DockStyle.Top;
        nameInput.Name = "DeviceGroupName";
        nameInput.AccessibleName = "分组名称";
        nameInput.Dock = DockStyle.Fill;
        nameSurface.Controls.Add(nameInput);
        fields.Controls.Add(nameLabel, 0, 0);
        fields.Controls.Add(nameSurface, 1, 0);
        groupViewport.Controls.Add(fields);
        Tabs.TabPages.Add(groupTab);
    }
}
