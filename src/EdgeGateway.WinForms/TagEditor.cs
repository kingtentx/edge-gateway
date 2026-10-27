using EdgeGateway.Model;
using EdgeGateway.Application;
using System.Globalization;

namespace EdgeGateway.WinForms;

internal sealed partial class TagEditor : EditorDialog
{
    public TagConfiguration Value { get; }
    private readonly DriverDescriptor _driver;
    private DriverMethodDescriptor? _priorMethod;
    private bool _initializing = true;

    public TagEditor() : base("采集点位", "定义地址、换算与上报规则。")
    { Value = new(); _driver = null!; InitializeComponent(); }

    public TagEditor(TagConfiguration value, DriverDescriptor driver)
        : base("采集点位", "定义地址与数据类型，再配置换算、报警和上报规则。")
    {
        Value = value; _driver = driver;
        InitializeComponent();
        _name.Text = value.Name; _address.Text = value.Address; _unit.Text = value.Unit;
        _enabled.Checked = value.Enabled; _uploadEnabled.Checked = value.UploadEnabled;
        _writable.Text = driver.SupportsWrite ? "允许手动写入此点位" : "当前驱动不提供写入能力";
        _writable.Checked = value.Writable && driver.SupportsWrite; _writable.Enabled = driver.SupportsWrite;
        EditorLayout.ConfigureDropDown(_readMethod, 680, 360);
        EditorLayout.ConfigureDropDown(_writeMethod, 680, 280);
        _readMethod.Items.AddRange(driver.Methods.Select(x => new MethodChoice(x)).Cast<object>().ToArray());
        var selectedName = string.IsNullOrEmpty(value.ReadMethod) ? driver.DefaultReadMethod : value.ReadMethod;
        var selectedMethod = driver.Methods.FirstOrDefault(x => x.Name == selectedName) ?? driver.Methods.FirstOrDefault(x => !x.IsDiagnostic);
        if (selectedMethod is not null) _readMethod.SelectedItem = _readMethod.Items.Cast<MethodChoice>().First(x => x.Method.Name == selectedMethod.Name);
        if (!string.IsNullOrEmpty(value.ReadMethod) && !driver.Methods.Any(x => x.Name == value.ReadMethod) && driver.IsLegacy)
        {
            var unknown = new MethodChoice(new DriverMethodDescriptor(value.ReadMethod, "未找到原方法", "请选择当前驱动提供的实际方法。"));
            _readMethod.Items.Add(unknown); _readMethod.SelectedItem = unknown;
        }
        BindChoice(_type, SupportedTypes(driver, selectedMethod).Cast<object>().ToArray(), value.DataType);
        BindChoice(_area, Enum.GetValues<ModbusArea>().Cast<object>().ToArray(), value.Area);
        BindChoice(_byteOrder, Enum.GetValues<RegisterByteOrder>().Cast<object>().ToArray(), value.ByteOrder);
        BindChoice(_endian, Enum.GetValues<ProtocolEndian>().Cast<object>().ToArray(), value.Endian);
        _writeMethod.Items.Add(new MethodChoice(new DriverMethodDescriptor("", "使用读取方法", "")));
        _writeMethod.Items.AddRange(driver.Methods.Where(x => x.SupportsWrite).Select(x => new MethodChoice(x)).Cast<object>().ToArray());
        _writeMethod.SelectedItem = _writeMethod.Items.Cast<MethodChoice>().FirstOrDefault(x => x.Method.Name == value.WriteMethod) ?? _writeMethod.Items[0];
        _writeMethod.Enabled = driver.SupportsWrite;
        SetFieldVisible(_readMethod, driver.Methods.Count > 0);
        SetFieldVisible(_endian, driver.IsLegacy);
        SetFieldVisible(_area, !driver.IsLegacy && driver.Id != "simulator");
        SetFieldVisible(_byteOrder, !driver.IsLegacy && driver.Id != "simulator");
        SetFieldVisible(_writeMethod, driver.IsLegacy && driver.SupportsWrite);
        _scale.Text = value.Scale.ToString("G17", CultureInfo.InvariantCulture);
        _offset.Text = value.Offset.ToString("G17", CultureInfo.InvariantCulture);
        _low.Text = value.LowLimit?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
        _high.Text = value.HighLimit?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
        _description.Text = value.Description; _expression.Text = value.Expression;
        _scanInterval.Value = Math.Clamp(value.ScanIntervalMs, 0, 3600000);
        _deadband.Text = value.Deadband.ToString("R", CultureInfo.InvariantCulture);
        previewButton.Click += (_, _) =>
        {
            var result = NumericExpression.Preview(_expression.Text, previewInput.Text);
            previewResult.Text = result.Success ? "计算结果：" + result.Value?.ToString(CultureInfo.InvariantCulture) : "无法计算：" + result.Error;
            previewResult.ForeColor = result.Success ? UiTheme.Ink : Color.Firebrick;
        };
        _readMethod.SelectedIndexChanged += (_, _) => UpdateMethod();
        _type.SelectedIndexChanged += (_, _) => UpdateCalculationFields();
        UpdateMethod(); _initializing = false;
    }

    internal static TagConfiguration CreateDefaultTag(DriverDescriptor driver, string name)
    {
        var method = driver.Methods.FirstOrDefault(x => x.Name == driver.DefaultReadMethod && !x.IsDiagnostic) ?? driver.Methods.FirstOrDefault(x => !x.IsDiagnostic);
        return new TagConfiguration
        {
            Name = name, ReadMethod = method?.Name ?? "", Address = method?.DefaultAddress ?? (driver.Id == "simulator" ? "sine" : "0"),
            DataType = method?.DefaultDataType ?? (driver.Id == "simulator" ? TagDataType.Float64 : TagDataType.UInt16),
            Endian = driver.IsLegacy ? ProtocolEndian.None : ProtocolEndian.FromByteOrder
        };
    }

    private void UpdateMethod()
    {
        var method = (_readMethod.SelectedItem as MethodChoice)?.Method;
        var oldType = _type.SelectedItem is TagDataType selected ? selected : Value.DataType;
        var choices = SupportedTypes(_driver, method);
        _type.Items.Clear(); _type.Items.AddRange(choices.Cast<object>().ToArray());
        var preferred = _initializing ? Value.DataType : choices.Contains(oldType) ? oldType : method?.DefaultDataType ?? choices[0];
        _type.SelectedItem = choices.Contains(preferred) ? preferred : choices[0];
        if (!_initializing && method is not null && (string.IsNullOrWhiteSpace(_address.Text) || _address.Text == _priorMethod?.DefaultAddress)) _address.Text = method.DefaultAddress;
        _priorMethod = method;
        _methodHint.Text = _driver.IsLegacy
            ? (method is null ? "请选择驱动提供的读取方法。" : (method.IsDiagnostic ? "诊断方法：此方法仅用于自检，不代表现场设备数据。\n" : "") + method.Description)
              + (method?.Name == "ReadMultiple" ? "\n批量读取返回结构化数据，倍率固定为 1、偏移为 0，不设置数值报警。" : "")
              + "\n协议地址格式由所选方法确定。FromByteOrder 保留原字节序设置；可选择明确的协议字节序。"
            : _driver.Id == "simulator" ? "模拟地址：sine、ramp、random、constant:数值、bool。"
              : "Modbus 地址从 0 开始。Coil / DiscreteInput 使用 Boolean；InputRegister / DiscreteInput 不可写。";
        UpdateCalculationFields();
    }

    private void UpdateCalculationFields()
    {
        if (_scale is null || _type.SelectedItem is not TagDataType type) return;
        var scalable = IsNumericType(type) && !UsesBatchRead;
        _scale.Enabled = _offset.Enabled = _low.Enabled = _high.Enabled = scalable;
        if (_expression is not null) _expression.Enabled = scalable;
        if (_deadband is not null) _deadband.Enabled = scalable;
        if (!scalable) { _scale.Text = "1"; _offset.Text = "0"; _low.Clear(); _high.Clear(); }
        if (!scalable && _expression is not null) _expression.Clear();
        if (!scalable && _deadband is not null) _deadband.Text = "0";
    }

    protected override void Commit()
    {
        Value.Name = Required(_name, "点位名称"); Value.Address = _driver.IsLegacy ? _address.Text.Trim() : Required(_address, "点位地址"); Value.Unit = _unit.Text.Trim();
        Value.DataType = (TagDataType)_type.SelectedItem!;
        Value.Area = (ModbusArea)_area.SelectedItem!; Value.ByteOrder = (RegisterByteOrder)_byteOrder.SelectedItem!; Value.Endian = (ProtocolEndian)_endian.SelectedItem!;
        Value.ReadMethod = _driver.Methods.Count > 0 ? ((_readMethod.SelectedItem as MethodChoice)?.Method.Name ?? "") : "";
        Value.WriteMethod = _driver.IsLegacy ? ((_writeMethod.SelectedItem as MethodChoice)?.Method.Name ?? "") : "";
        Value.Enabled = _enabled.Checked; Value.UploadEnabled = _uploadEnabled.Checked; Value.Writable = _writable.Checked;
        if (_driver.IsLegacy && !_driver.Methods.Any(x => x.Name == Value.ReadMethod)) throw new EditorValidationException("请选择当前驱动提供的读取方法。", _readMethod);
        if (Value.Writable && _driver.IsLegacy)
        {
            var writeMethod = Value.WriteMethod.Length == 0 ? Value.ReadMethod : Value.WriteMethod;
            if (!_driver.Methods.Any(x => x.Name == writeMethod && x.SupportsWrite)) throw new EditorValidationException("所选方法不支持写入，请选择可写方法或关闭写入权限。", _writeMethod);
        }
        if (IsNumericType(Value.DataType) && !UsesBatchRead)
        {
            Value.Scale = ParseNumber(_scale, "倍率"); Value.Offset = ParseNumber(_offset, "偏移量");
            Value.LowLimit = string.IsNullOrWhiteSpace(_low.Text) ? null : ParseNumber(_low, "报警下限");
            Value.HighLimit = string.IsNullOrWhiteSpace(_high.Text) ? null : ParseNumber(_high, "报警上限");
            if (Value.LowLimit.HasValue && Value.HighLimit.HasValue && Value.LowLimit.Value > Value.HighLimit.Value) throw new EditorValidationException("报警下限不能高于上限。", _low);
            if (Value.Writable && Value.Scale == 0) throw new EditorValidationException("允许写入时倍率不能为 0。", _scale);
        }
        else { Value.Scale = 1; Value.Offset = 0; Value.LowLimit = null; Value.HighLimit = null; }
        Value.Expression = _expression.Text.Trim();
        if (NumericExpression.Validate(Value.Expression) is string expressionError) throw new EditorValidationException(expressionError, _expression);
        if (Value.Expression.Length > 0 && Value.Writable) throw new EditorValidationException("计算表达式点不能直接反算写入，请关闭写入权限，或使用独立原始点位写入。", _writable);
        Value.ScanIntervalMs = (int)_scanInterval.Value;
        if (Value.ScanIntervalMs != 0 && Value.ScanIntervalMs < 100) throw new EditorValidationException("点位周期须为 0 或 100–3600000 ms。", _scanInterval);
        Value.Deadband = ParseNumber(_deadband, "上报死区");
        if (Value.Deadband < 0) throw new EditorValidationException("上报死区不能为负数。", _deadband);
        Value.Description = _description.Text.Trim();
    }

    internal void VerifyBindingsForSmoke()
    {
        var originalId = Value.Id;
        var originalMethod = (_readMethod.SelectedItem as MethodChoice)?.Method.Name;
        var originalType = (TagDataType)_type.SelectedItem!;
        foreach (var method in _driver.Methods)
        {
            _readMethod.SelectedItem = _readMethod.Items.Cast<MethodChoice>().First(x => x.Method.Name == method.Name);
            Commit();
            if (Value.ReadMethod != method.Name) throw new InvalidOperationException($"{_driver.Id}: method {method.Name} was not saved.");
            foreach (var type in SupportedTypes(_driver, method))
                if (!_type.Items.Contains(type)) throw new InvalidOperationException($"{_driver.Id}: missing type {type} for method {method.Name}.");
        }
        if (originalMethod is not null) _readMethod.SelectedItem = _readMethod.Items.Cast<MethodChoice>().First(x => x.Method.Name == originalMethod);
        if (_type.Items.Contains(originalType)) _type.SelectedItem = originalType;
        Commit();
        if (Value.Id != originalId) throw new InvalidOperationException("Tag editor changed point identity.");
    }

    private static IReadOnlyList<TagDataType> SupportedTypes(DriverDescriptor driver, DriverMethodDescriptor? method)
    {
        if (method?.SupportedDataTypes.Count > 0) return method.SupportedDataTypes;
        if (driver.SupportedDataTypes.Count > 0) return driver.SupportedDataTypes;
        return Enum.GetValues<TagDataType>().Take(7).ToArray();
    }

    private bool UsesBatchRead => (_readMethod.SelectedItem as MethodChoice)?.Method.Name == "ReadMultiple";

    internal static bool IsNumericType(TagDataType type) => type is TagDataType.Bit or TagDataType.Int8 or TagDataType.UInt8 or TagDataType.Int16 or TagDataType.UInt16
        or TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Float32 or TagDataType.Float64
        or TagDataType.Bcd16 or TagDataType.Bcd32 or TagDataType.TimeStampMs or TagDataType.TimeStampS;
    internal static bool IsStringType(TagDataType type) => type is TagDataType.AsciiString or TagDataType.Utf8String or TagDataType.Gb2312String;
    private static double ParseNumber(TextBox input, string name)
    {
        if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw new EditorValidationException(name + "必须为有效的有限数值。", input);
        return value;
    }
    private sealed record MethodChoice(DriverMethodDescriptor Method)
    {
        public override string ToString() => (Method.IsDiagnostic ? "[诊断] " : "")
            + (string.IsNullOrEmpty(Method.Description) ? Method.DisplayName : Method.Description)
            + (Method.Name.Length == 0 ? "" : " · " + Method.Name);
    }
}
