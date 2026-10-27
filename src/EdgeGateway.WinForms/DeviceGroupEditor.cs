namespace EdgeGateway.WinForms;

internal sealed partial class DeviceGroupEditor : EditorDialog
{
    internal string Value { get; private set; } = "";
    public DeviceGroupEditor() : base("设备分组", "空分组也会保存；重命名同步更新该组的设备。")
    {
        InitializeComponent();
        ConfigureWindow(new Size(660, 380), new Size(580, 340));
    }
    public DeviceGroupEditor(string name) : this() { nameInput.Text = name; }
    protected override void Commit()
    {
        Value = Required(nameInput, "分组名称");
        if (Value.Length > 80) throw new EditorValidationException("分组名称不能超过 80 个字符。", nameInput);
    }
}
