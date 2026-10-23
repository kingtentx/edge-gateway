using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using System.Globalization;
using System.Text.Json;

namespace EdgeGateway.WinForms;

/// <summary>Edits the selected device's acquisition and driver settings as an isolated draft.</summary>
internal sealed class DeviceConfigurationDialog : EditorDialog
{
    private readonly DeviceConfiguration _original;
    private readonly IDriverFactory _factory;
    private readonly DriverDescriptor _descriptor;
    private readonly bool _readOnly;
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);
    private readonly List<Control> _connectionInputs = [];
    private readonly CheckBox _enabled;
    private readonly NumericUpDown _cycle, _timeout;
    private readonly DriverParameterPanel? _parameters;

    /// <summary>Available only after a successful commit; never aliases the caller's device or tags.</summary>
    public DeviceConfiguration? Value { get; private set; }
    internal IReadOnlyCollection<Control> SmokeParameterInputs => _parameters?.Inputs ?? _connectionInputs;

    public DeviceConfigurationDialog(DeviceConfiguration device, IDriverFactory factory, bool readOnly = false)
        : base(readOnly ? "查看设备配置" : "设备配置", readOnly
            ? "采集运行期间仅可查看当前设备配置。停止全部运行设备后，可在此修改启用状态、采集周期、通信超时与驱动连接参数。"
            : "配置当前设备的启用状态、采集周期、通信超时与驱动连接参数。点击“确定”完成校验并自动保存；取消不修改原配置。")
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _descriptor = factory.Descriptor;
        if (!string.Equals(device.DriverId, _descriptor.Id, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("所选驱动与当前设备不一致，无法打开设备配置。", nameof(factory));
        _original = Clone(device);
        Text = (readOnly ? "查看设备配置" : "设备配置") + " · " + _original.Name;
        if (_original.Properties is null) throw new InvalidDataException("设备驱动参数 Properties 缺失，请检查配置文件。");
        _readOnly = readOnly;

        var acquisition = AddTab("采集设置");
        TabOf(acquisition).Name = "AcquisitionSettings";
        _enabled = Register("Enabled", new CheckBox
        {
            Text = "启用设备", Checked = _original.Enabled, AutoSize = true,
            MinimumSize = new Size(0, 32)
        });
        var name = Register("Name", TextInput(_original.Name)); name.ReadOnly = true;
        var driver = Register("DriverId", TextInput(_descriptor.Name + " · " + _original.DriverId)); driver.ReadOnly = true;
        _cycle = Register("PollIntervalMs", Number(_original.PollIntervalMs, 100, 3_600_000));
        _timeout = Register("TimeoutMs", Number(_original.TimeoutMs, 100, 120_000));
        Field(acquisition, "启用状态", _enabled);
        Field(acquisition, "设备名称", name);
        Field(acquisition, "采集驱动", driver);
        Field(acquisition, "采集周期 (ms)", _cycle);
        Field(acquisition, "通信超时 (ms)", _timeout);
        Hint(acquisition, "启用后可在设备行点击“启动”；取消勾选后禁止启动该设备。采集周期为 100–3600000 ms，通信超时为 100–120000 ms。");
        if (!string.IsNullOrWhiteSpace(_descriptor.Prerequisites))
            Hint(acquisition, "运行条件：" + _descriptor.Prerequisites);

        if (_descriptor.IsLegacy || _descriptor.Parameters.Count > 0)
        {
            _parameters = new DriverParameterPanel();
            var parameterPage = new TabPage("驱动参数") { Name = "DriverConfigurationParameters", BackColor = Color.White };
            parameterPage.Controls.Add(_parameters);
            Tabs.TabPages.Add(parameterPage);
            _parameters.LoadParameters(_descriptor, BoundDevice(), _original.Properties, "采集设置");
            _parameters.SetReadOnly(readOnly);
        }
        else if (_descriptor.Id == "modbus-tcp")
        {
            var connection = AddTab("网络连接"); TabOf(connection).Name = "NativeConnection";
            AddConnection(connection, "Host", "主机地址", TextInput(_original.Host));
            AddConnection(connection, "Port", "TCP 端口", Number(_original.Port, 1, 65535));
            AddConnection(connection, "UnitId", "Modbus 站号", Number(_original.UnitId, 1, 247));
            Hint(connection, "主机填写设备 IP 或域名；TCP 端口为 1–65535，站号为 1–247。寄存器地址、类型与字节序在点位中配置。");
        }
        else if (_descriptor.Id == "modbus-rtu")
        {
            var connection = AddTab("串口连接"); TabOf(connection).Name = "NativeConnection";
            AddConnection(connection, "SerialPortName", "串口名称", TextInput(_original.SerialPortName));
            AddConnection(connection, "BaudRate", "波特率", Number(_original.BaudRate, 300, 4_000_000));
            AddConnection(connection, "DataBits", "数据位", Number(_original.DataBits, 5, 8));
            AddConnection(connection, "Parity", "校验位", Choice(["None", "Odd", "Even", "Mark", "Space"], _original.Parity));
            AddConnection(connection, "StopBits", "停止位", Choice(["One", "Two"], _original.StopBits));
            AddConnection(connection, "UnitId", "Modbus 站号", Number(_original.UnitId, 1, 247));
            Hint(connection, "Modbus RTU 二进制通信要求 8 个数据位，停止位选择 One 或 Two；同一串口当前只允许一个启用设备。");
        }
        else
        {
            Hint(acquisition, _descriptor.Id == "simulator"
                ? "模拟数据驱动无需网络或串口通信参数。模拟函数与地址在各点位中配置。"
                : "当前驱动未公开额外连接参数。可在本页调整采集周期与通信超时。");
        }

        Tabs.SelectedTab = TabOf(acquisition);
        _cycle.ValueChanged += (_, _) => _parameters?.RefreshBindings(BoundDevice());
        _timeout.ValueChanged += (_, _) => _parameters?.RefreshBindings(BoundDevice());
        if (readOnly)
        {
            foreach (var input in _fields.Values)
            {
                if (input is TextBoxBase text) text.ReadOnly = true;
                else input.Enabled = false;
            }
            SetReadOnlyView();
        }
    }

    protected override void Commit()
    {
        Value = null;
        if (_readOnly) throw new InvalidOperationException("当前设备配置为只读，停止采集后才能修改。");
        var candidate = Clone(_original);
        candidate.Enabled = _enabled.Checked;
        candidate.PollIntervalMs = IntegerValue(_cycle);
        candidate.TimeoutMs = IntegerValue(_timeout);
        if (_parameters is not null) candidate.Properties = _parameters.CaptureProperties();
        else if (_descriptor.Id == "modbus-tcp")
        {
            candidate.Host = _fields["Host"].Text.Trim();
            candidate.Port = IntegerValue((NumericUpDown)_fields["Port"]);
            candidate.UnitId = IntegerValue((NumericUpDown)_fields["UnitId"]);
        }
        else if (_descriptor.Id == "modbus-rtu")
        {
            candidate.SerialPortName = _fields["SerialPortName"].Text.Trim();
            candidate.BaudRate = IntegerValue((NumericUpDown)_fields["BaudRate"]);
            candidate.DataBits = IntegerValue((NumericUpDown)_fields["DataBits"]);
            candidate.Parity = ((ComboBox)_fields["Parity"]).SelectedItem?.ToString() ?? "";
            candidate.StopBits = ((ComboBox)_fields["StopBits"]).SelectedItem?.ToString() ?? "";
            candidate.UnitId = IntegerValue((NumericUpDown)_fields["UnitId"]);
        }

        var errors = new List<string>();
        if (candidate.PollIntervalMs is < 100 or > 3_600_000) errors.Add("采集周期必须为 100–3600000 ms。");
        if (candidate.TimeoutMs is < 100 or > 120_000) errors.Add("通信超时必须为 100–120000 ms。");
        // Factory validation also checks tags. They are outside this editor, so validate
        // an isolated copy without tags while keeping every driver property, including
        // unknown keys that the factory may reject. Never remove keys to evade validation.
        var validation = Clone(candidate);
        validation.Tags = [];
        errors.AddRange(_factory.Validate(validation));
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Distinct(StringComparer.Ordinal)));
        Value = candidate;
    }

    internal void VerifyBindingsForSmoke()
    {
        if (_fields["Enabled"] is not CheckBox enabled || !ReferenceEquals(enabled, _enabled))
            throw new InvalidOperationException("Device configuration lost its enabled-state field.");
        if (_fields["Name"].Text != _original.Name || _fields["Name"] is not TextBox { ReadOnly: true }
            || _fields["DriverId"] is not TextBox { ReadOnly: true }
            || !_fields["DriverId"].Text.EndsWith(" · " + _original.DriverId, StringComparison.Ordinal))
            throw new InvalidOperationException("Device configuration lost its read-only identity fields.");
        _parameters?.VerifyBindingsForSmoke(_descriptor, BoundDevice(), verifyInitialSelection: false);
        if (_readOnly)
        {
            if (Value is not null || SmokeCommands.Count != 1 || SmokeCommands[0].Text != "关闭"
                || !ReferenceEquals(AcceptButton, SmokeCommands[0]))
                throw new InvalidOperationException("Read-only device configuration exposes an apply action.");
            foreach (var input in _fields.Values.Concat(SmokeParameterInputs))
                if (input is TextBoxBase text ? !text.ReadOnly : input.Enabled)
                    throw new InvalidOperationException("Read-only device configuration exposes an editable field.");
        }
    }

    internal void SetFieldForSmoke(string key, string value)
    {
        if (_readOnly) throw new InvalidOperationException("当前设备配置为只读，不能修改字段。");
        Control input;
        if (key.StartsWith("Properties.", StringComparison.Ordinal))
            input = _parameters?.Inputs.Single(x => x.Name == "DriverParameter_" + key[11..])
                ?? throw new ArgumentException("当前驱动没有参数字段。", nameof(key));
        else if (!_fields.TryGetValue(key, out input!)) throw new ArgumentException("未找到配置字段：" + key, nameof(key));
        if (!input.Enabled || input is TextBoxBase { ReadOnly: true })
            throw new InvalidOperationException("该配置字段为只读。");
        switch (input)
        {
            case NumericUpDown number: number.Value = decimal.Parse(value, CultureInfo.InvariantCulture); break;
            case ComboBox choice:
                if (!choice.Items.Contains(value)) throw new ArgumentException("不存在枚举选项：" + value, nameof(value));
                choice.SelectedItem = value; break;
            case CheckBox toggle: toggle.Checked = bool.Parse(value); break;
            default: input.Text = value; break;
        }
    }

    internal DeviceConfiguration CommitForSmoke()
    {
        Commit();
        return Value ?? throw new InvalidOperationException("设备配置尚未通过验证。");
    }

    private DeviceConfiguration BoundDevice() => new()
    {
        Id = _original.Id, PollIntervalMs = IntegerValue(_cycle), TimeoutMs = IntegerValue(_timeout)
    };

    private T Register<T>(string key, T input) where T : Control
    {
        input.Name = "DeviceConfiguration_" + key;
        _fields.Add(key, input);
        return input;
    }

    private void AddConnection(TableLayoutPanel table, string key, string caption, Control input)
    {
        Field(table, caption, Register(key, input));
        _connectionInputs.Add(input);
    }

    private static NumericUpDown Number(int value, int minimum, int maximum) =>
        NumberInput(value, Math.Min(value, minimum), Math.Max(value, maximum));

    private static ComboBox Choice(string[] options, string selected)
    {
        var values = options.Cast<object>().ToList();
        if (!options.Contains(selected, StringComparer.Ordinal)) values.Add(selected);
        return SelectInput(values.ToArray(), selected);
    }

    private static int IntegerValue(NumericUpDown input)
    {
        if (input.Value != decimal.Truncate(input.Value)) throw new InvalidOperationException("配置数值必须为整数。");
        return checked((int)input.Value);
    }

    private static TabPage TabOf(TableLayoutPanel table) => (TabPage)table.Parent!.Parent!;
    private static DeviceConfiguration Clone(DeviceConfiguration value) =>
        JsonSerializer.Deserialize<DeviceConfiguration>(JsonSerializer.Serialize(value, JsonConfigurationStore.SerializerOptions),
            JsonConfigurationStore.SerializerOptions)!;
}
