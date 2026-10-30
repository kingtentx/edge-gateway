using EdgeGateway.Model;
using System.Globalization;

namespace EdgeGateway.WinForms;

internal sealed partial class DeviceParameterDialog : EditorDialog
{
    public string ParameterKey => keyInput.Text.Trim();
    public string ParameterValue => valueInput.Text;
    public DeviceParameterMetadata Metadata => new() { Name = nameInput.Text.Trim(), TypeName = typeInput.SelectedItem?.ToString() ?? "String", Description = descriptionInput.Text.Trim() };
    private readonly Func<string, bool> _duplicate;
    private readonly bool _declared;
    public DeviceParameterDialog() : this("", "", false, false, _ => false) { }
    public DeviceParameterDialog(string key, string value, bool declared, bool secret, Func<string, bool> duplicate, DeviceParameterMetadata? metadata = null)
        : base(key.Length == 0 ? "添加设备参数" : "编辑设备参数", "参数标识须与驱动读取的名称一致；数值和选项按驱动说明填写。")
    {
        _duplicate = duplicate; _declared = declared; InitializeComponent();
        ConfigureWindow(new Size(600, 440), new Size(480, 340));
        keyInput.Text = key; keyInput.ReadOnly = declared;
        valueInput.Text = value;
        nameInput.Text = metadata?.Name ?? "";
        descriptionInput.Text = metadata?.Description ?? "";
        typeInput.Items.AddRange(new object[] { "String", "Boolean", "Byte", "Int16", "Int32", "Int64", "UInt16", "UInt32", "UInt64", "Single", "Double", "Decimal" });
        var type = metadata?.TypeName ?? "String";
        if (!typeInput.Items.Contains(type)) typeInput.Items.Add(type);
        typeInput.SelectedItem = type; typeInput.Enabled = !declared;
        valueInput.UseSystemPasswordChar = secret || DriverCustomParametersView.IsSecret(key);
        keyInput.TextChanged += (_, _) => valueInput.UseSystemPasswordChar = secret || DriverCustomParametersView.IsSecret(keyInput.Text);
    }
    internal void SetForSmoke(string key, string value) { keyInput.Text = key; valueInput.Text = value; if (nameInput.Text.Length == 0) nameInput.Text = key; }
    internal void SetMetadataForSmoke(string name, string type, string description)
    { nameInput.Text = name; typeInput.SelectedItem = type; descriptionInput.Text = description; }
    protected override void Commit()
    {
        if (string.IsNullOrWhiteSpace(nameInput.Text)) throw new EditorValidationException("参数名称不能为空。", nameInput);
        if (nameInput.Text.Trim().Length > 120 || descriptionInput.Text.Length > 1000) throw new EditorValidationException("参数名称最多 120 字，说明最多 1000 字。", nameInput);
        if (!_declared && Metadata.TypeName != "String")
        {
            var type = Metadata.TypeName switch { "Boolean" => typeof(bool), "Byte" => typeof(byte), "Int16" => typeof(short), "Int32" => typeof(int), "Int64" => typeof(long), "UInt16" => typeof(ushort), "UInt32" => typeof(uint), "UInt64" => typeof(ulong), "Single" => typeof(float), "Double" => typeof(double), "Decimal" => typeof(decimal), _ => typeof(string) };
            try { var parsed = Convert.ChangeType(ParameterValue, type, CultureInfo.InvariantCulture); if (parsed is double d && !double.IsFinite(d) || parsed is float f && !float.IsFinite(f)) throw new FormatException(); }
            catch (Exception ex) when (ex is FormatException or OverflowException) { throw new EditorValidationException("参数值与所选类型不符。", valueInput); }
        }
        if (ParameterKey.Length == 0) throw new EditorValidationException("参数标识不能为空。", keyInput);
        if (_duplicate(ParameterKey)) throw new EditorValidationException("参数标识已存在。", keyInput);
    }
}
