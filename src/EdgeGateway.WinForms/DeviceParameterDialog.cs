namespace EdgeGateway.WinForms;

internal sealed partial class DeviceParameterDialog : EditorDialog
{
    public string ParameterKey => keyInput.Text.Trim();
    public string ParameterValue => valueInput.Text;
    private readonly Func<string, bool> _duplicate;
    public DeviceParameterDialog() : this("", "", false, false, _ => false) { }
    public DeviceParameterDialog(string key, string value, bool declared, bool secret, Func<string, bool> duplicate)
        : base(key.Length == 0 ? "添加设备参数" : "编辑设备参数", "参数标识须与驱动读取的名称一致；数值和选项按驱动说明填写。")
    {
        _duplicate = duplicate; InitializeComponent();
        ConfigureWindow(new Size(620, 360), new Size(500, 320));
        keyInput.Text = key; keyInput.ReadOnly = declared;
        valueInput.Text = value;
        valueInput.UseSystemPasswordChar = secret || DriverCustomParametersView.IsSecret(key);
        keyInput.TextChanged += (_, _) => valueInput.UseSystemPasswordChar = secret || DriverCustomParametersView.IsSecret(keyInput.Text);
    }
    internal void SetForSmoke(string key, string value) { keyInput.Text = key; valueInput.Text = value; }
    protected override void Commit()
    {
        if (ParameterKey.Length == 0) throw new EditorValidationException("参数标识不能为空。", keyInput);
        if (_duplicate(ParameterKey)) throw new EditorValidationException("参数标识已存在。", keyInput);
    }
}
