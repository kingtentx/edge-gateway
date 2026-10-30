using EdgeGateway.Application;
using EdgeGateway.Model;
using System.Globalization;

namespace EdgeGateway.WinForms;

/// <summary>Creates or edits every device setting through one isolated, driver-aware draft.</summary>
internal sealed partial class DeviceConfigurationDialog : EditorDialog
{
    private readonly DeviceConfiguration _original;
    private readonly IDriverCatalog _drivers;
    private readonly bool _readOnly, _isNew;
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceConfiguration> _driverDrafts = new(StringComparer.OrdinalIgnoreCase);
    private DriverDescriptor? _descriptor;

    /// <summary>Available only after a successful commit; never aliases the caller's device or tags.</summary>
    public DeviceConfiguration? Value { get; private set; }
    internal IReadOnlyCollection<Control> SmokeParameterInputs => Array.Empty<Control>();
    internal Guid? SmokeChannelId => (_channel.SelectedItem as ChannelOption)?.Id;

    public DeviceConfigurationDialog() : base("设备配置", "配置设备与采集参数。")
    {
        _original = new(); _drivers = null!;
        InitializeComponent();
    }

    public DeviceConfigurationDialog(DeviceConfiguration device, IDriverCatalog drivers, bool readOnly = false, bool isNew = false,
        IReadOnlyList<ChannelConfiguration>? channels = null, IReadOnlyList<string>? groups = null)
        : base(readOnly ? "查看设备配置" : isNew ? "添加采集设备" : "设备配置", readOnly
            ? "浏览设备与连接参数。当前为只读视图，不会更改配置。"
            : "选择驱动并填写连接参数，保存后生效。设备可在列表中单独启动。")
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(drivers);
        _drivers = drivers;
        _original = Clone(device);
        Text = (readOnly ? "查看设备配置" : isNew ? "添加采集设备" : "设备配置") + " · " + _original.Name;
        if (_original.Properties is null) throw new InvalidDataException("设备驱动参数 Properties 缺失，请检查配置文件。");
        _readOnly = readOnly; _isNew = isNew;
        _driverDrafts[_original.DriverId] = Clone(_original);

        InitializeComponent();
        Register("Enabled", _enabled); Register("Name", _name); Register("Group", _group);
        Register("ChannelId", _channel); Register("DriverId", _driver);
        Register("PollIntervalMs", _cycle); Register("TimeoutMs", _timeout);
        Register("ScanMode", _scanMode); Register("AutoDemotionEnabled", _autoDemotion);
        Register("DemotionFailureCount", _demotionCount); Register("DemotionIntervalMs", _demotionInterval);
        _group.Items.AddRange((groups ?? []).Append(_original.Group).Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        _enabled.Checked = _original.Enabled; _name.Text = _original.Name; _group.Text = _original.Group;
        _cycle.Value = Math.Clamp(_original.PollIntervalMs, 100, 3600000);
        _timeout.Value = Math.Clamp(_original.TimeoutMs, 100, 120000);
        _autoDemotion.Checked = _original.AutoDemotionEnabled;
        _demotionCount.Value = Math.Clamp(_original.DemotionFailureCount, 1, 30);
        _demotionInterval.Value = Math.Clamp(_original.DemotionIntervalMs, 100, 3600000);
        EditorLayout.ConfigureDropDown(_channel, 520, 320);
        _channel.Items.Add(new ChannelOption(null, "独立通信"));
        _channel.Items.AddRange((channels ?? []).Select(c => new ChannelOption(c.Id, c.Name)).Cast<object>().ToArray());
        if (_original.ChannelId.HasValue && !_channel.Items.Cast<ChannelOption>().Any(c => c.Id == _original.ChannelId))
            _channel.Items.Add(new ChannelOption(_original.ChannelId, "现有通道 · " + _original.ChannelId));
        _channel.SelectedItem = _channel.Items.Cast<ChannelOption>().First(c => c.Id == _original.ChannelId);
        EditorLayout.ConfigureDropDown(_driver, 620, 440);
        _driver.Format += (_, e) => { if (e.ListItem is DriverDescriptor descriptor) e.Value = descriptor.Name + "  ·  " + descriptor.Id; };
        var catalog = _drivers.Drivers.ToArray();
        _driver.Items.AddRange(catalog.Cast<object>().ToArray());
        _driver.SelectedIndex = Array.FindIndex(catalog, x => string.Equals(x.Id, _original.DriverId, StringComparison.OrdinalIgnoreCase));
        if (_driver.SelectedIndex < 0) throw new InvalidOperationException("未找到设备使用的采集驱动：" + _original.DriverId);
        _scanMode.Items.AddRange(Enum.GetValues<DeviceScanMode>().Cast<object>().ToArray());
        _scanMode.SelectedItem = _original.ScanMode;
        _scanMode.Format += (_, e) => e.Value = e.ListItem is DeviceScanMode.TagRate ? "按点位周期扫描" : "按设备周期扫描";
        _driver.SelectedIndexChanged += (_, _) =>
        {
            try { UpdateProtocolFields(); }
            catch (Exception exception)
            {
                _driver.SelectedItem = _descriptor;
                MessageBox.Show(this, exception.Message, "驱动参数", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _cycle.ValueChanged += (_, _) => customParameters.RefreshBindings(BoundDevice());
        _timeout.ValueChanged += (_, _) => customParameters.RefreshBindings(BoundDevice());
        UpdateProtocolFields();
        Tabs.SelectedTab = isNew ? acquisitionTab : customParametersTab;
        if (isNew && !readOnly) SetConfirmText("添加设备");
        if (readOnly) SetReadOnlyView();
    }

    private void UpdateProtocolFields()
    {
        var descriptor = _driver.SelectedItem as DriverDescriptor
            ?? throw new InvalidOperationException("请选择采集驱动。");
        if (ReferenceEquals(descriptor, _descriptor)) return;
        if (_readOnly && _descriptor is not null)
        {
            _driver.SelectedItem = _descriptor;
            return;
        }
        if (_descriptor is not null) CaptureDriverDraft();
        ClearValidationFeedback();
        Tabs.SuspendLayout();
        try
        {
            _descriptor = descriptor;
            if (!_driverDrafts.TryGetValue(descriptor.Id, out var draft))
            {
                draft = Clone(_original);
                draft.DriverId = descriptor.Id;
                // Each protocol owns its properties. Keep the former protocol's unknown
                // keys in its draft, so switching back never silently discards them.
                draft.Properties = new Dictionary<string, string>(StringComparer.Ordinal);
                _driverDrafts.Add(descriptor.Id, draft);
            }
            var parameterDevice = Clone(draft);
            parameterDevice.PollIntervalMs = IntegerValue(_cycle);
            parameterDevice.TimeoutMs = IntegerValue(_timeout);
            customParameters.LoadDriver(descriptor, parameterDevice, _readOnly);
            Tabs.SelectedTab = customParametersTab;
            _driverDetails.Text = descriptor.Description
                + (string.IsNullOrWhiteSpace(descriptor.Prerequisites) ? "" : "\n运行条件：" + descriptor.Prerequisites)
                + (descriptor.RequiresSta ? "\n此驱动使用专用 STA 线程运行。" : "");
            ApplyReadOnlyState();
            Value = null;
        }
        finally { Tabs.ResumeLayout(true); }
    }

    private void ApplyReadOnlyState()
    {
        foreach (var input in _fields.Values)
        {
            if (input is TextBox text) text.ReadOnly = _readOnly;
            else input.Enabled = !_readOnly;
        }
    }

    private DeviceConfiguration CaptureDriverDraft()
    {
        var descriptor = _descriptor ?? throw new InvalidOperationException("请选择采集驱动。");
        var candidate = Clone(_driverDrafts[descriptor.Id]);
        customParameters.ApplyToDevice(candidate);
        _driverDrafts[descriptor.Id] = candidate;
        return candidate;
    }

    protected override void Commit()
    {
        Value = null;
        if (_readOnly) throw new InvalidOperationException("当前设备配置为只读，停止采集后才能修改。");
        var descriptor = _descriptor ?? throw new InvalidOperationException("请选择采集驱动。");
        var candidate = Clone(CaptureDriverDraft());
        candidate.Name = Required(_name, "设备名称");
        candidate.Group = _group.Text.Trim();
        if (candidate.Group.Length is 0 or > 80) throw new EditorValidationException("设备分组须为 1–80 个字符。", _group);
        candidate.DriverId = descriptor.Id;
        candidate.Enabled = _enabled.Checked;
        candidate.PollIntervalMs = IntegerValue(_cycle);
        candidate.TimeoutMs = IntegerValue(_timeout);
        candidate.ChannelId = (_channel.SelectedItem as ChannelOption)?.Id;
        candidate.ScanMode = (DeviceScanMode)_scanMode.SelectedItem!;
        candidate.AutoDemotionEnabled = _autoDemotion.Checked;
        candidate.DemotionFailureCount = IntegerValue(_demotionCount);
        candidate.DemotionIntervalMs = IntegerValue(_demotionInterval);
        if (_isNew) candidate.Tags = [TagEditor.CreateDefaultTag(descriptor, "采集点位")];
        var errors = new List<string>();
        if (candidate.PollIntervalMs is < 100 or > 3_600_000) errors.Add("采集周期必须为 100–3600000 ms。");
        if (candidate.TimeoutMs is < 100 or > 120_000) errors.Add("通信超时必须为 100–120000 ms。");
        // Factory validation also checks tags. They are outside this editor, so validate
        // an isolated copy without tags while keeping every driver property, including
        // unknown keys that the factory may reject. Never remove keys to evade validation.
        var validation = Clone(candidate);
        validation.Tags = [];
        errors.AddRange(_drivers.Validate(validation));
        if (errors.Count > 0)
        {
            var message = string.Join(Environment.NewLine, errors.Distinct(StringComparer.Ordinal));
            var target = ValidationField(errors[0]);
            if (target is not null) throw new EditorValidationException(message, target);
            throw new InvalidOperationException(message);
        }
        Value = candidate;
    }

    private Control? ValidationField(string error)
    {
        if (error.Contains("采集周期", StringComparison.Ordinal) || error.Contains("MinPeriod", StringComparison.Ordinal)) return _cycle;
        if (error.Contains("超时", StringComparison.Ordinal) || error.Contains("Timeout", StringComparison.Ordinal)) return _timeout;
        foreach (var (key, caption) in new[] { ("Host", "主机"), ("Port", "端口"), ("UnitId", "站号"),
                     ("SerialPortName", "串口名称"), ("BaudRate", "波特率"), ("DataBits", "数据位"), ("Parity", "校验位"), ("StopBits", "停止位") })
            if (error.Contains(caption, StringComparison.Ordinal) && _fields.TryGetValue(key, out var field)) return field;
        return null;
    }

    internal void VerifyBindingsForSmoke()
    {
        if (_fields["Enabled"] is not CheckBox enabled || !ReferenceEquals(enabled, _enabled))
            throw new InvalidOperationException("Device configuration lost its enabled-state field.");
        if (!ReferenceEquals(_fields["Name"], _name) || !ReferenceEquals(_fields["Group"], _group)
            || !ReferenceEquals(_fields["DriverId"], _driver))
            throw new InvalidOperationException("Device configuration lost its common device fields.");
        var descriptor = _descriptor ?? throw new InvalidOperationException("Device configuration has no selected driver.");
        if (!ReferenceEquals(_driver.SelectedItem, descriptor))
            throw new InvalidOperationException("Device configuration lost the current driver selection.");
        if (_readOnly)
        {
            if (Value is not null || SmokeCommands.Count != 1 || SmokeCommands[0].Text != "关闭"
                || !ReferenceEquals(AcceptButton, SmokeCommands[0]))
                throw new InvalidOperationException("Read-only device configuration exposes an apply action.");
            foreach (var input in _fields.Values.Concat(SmokeParameterInputs))
                if (input is TextBox text ? !text.ReadOnly : input.Enabled)
                    throw new InvalidOperationException("Read-only device configuration exposes an editable field.");
        }
    }

    internal IReadOnlyDictionary<string, string> ParameterValuesForSmoke => customParameters.Values;

    internal void SetFieldForSmoke(string key, string value)
    {
        if (_readOnly) throw new InvalidOperationException("当前设备配置为只读，不能修改字段。");
        if (key == "DriverId") { SelectDriverForSmoke(value); return; }
        Control input;
        var parameterKey = key.StartsWith("Properties.", StringComparison.Ordinal) ? key[11..] : key;
        if (customParameters.Values.ContainsKey(parameterKey))
        { customParameters.SetForSmoke(parameterKey, value); return; }
        if (!_fields.TryGetValue(key, out input!)) throw new ArgumentException("未找到配置字段：" + key, nameof(key));
        if (!input.Enabled || input is TextBox { ReadOnly: true })
            throw new InvalidOperationException("该配置字段为只读。");
        switch (input)
        {
            case NumericUpDown number: number.Value = decimal.Parse(value, CultureInfo.InvariantCulture); break;
            case ComboBox choice when choice.DropDownStyle == ComboBoxStyle.DropDown:
                choice.Text = value; break;
            case ComboBox choice:
                var option = choice.Items.Cast<object>().SingleOrDefault(item => item switch
                {
                    ChannelOption channel => value.Length == 0 ? channel.Id is null
                        : Guid.TryParse(value, out var id) && channel.Id == id,
                    Enum enumeration => string.Equals(enumeration.ToString(), value, StringComparison.Ordinal),
                    string text => string.Equals(text, value, StringComparison.Ordinal),
                    _ => false
                });
                if (option is null) throw new ArgumentException("不存在枚举或通道选项：" + value, nameof(value));
                choice.SelectedItem = option; break;
            case CheckBox toggle: toggle.Checked = bool.Parse(value); break;
            default: input.Text = value; break;
        }
    }

    internal void SelectDriverForSmoke(string driverId)
    {
        if (_readOnly) throw new InvalidOperationException("当前设备配置为只读，不能切换驱动。");
        _driver.SelectedItem = _driver.Items.Cast<DriverDescriptor>()
            .FirstOrDefault(x => string.Equals(x.Id, driverId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("未找到采集驱动：" + driverId, nameof(driverId));
    }

    internal DeviceConfiguration CommitForSmoke()
    {
        Commit();
        return Value ?? throw new InvalidOperationException("设备配置尚未通过验证。");
    }

    private DeviceConfiguration BoundDevice()
    {
        var device = Clone(_descriptor is not null ? _driverDrafts[_descriptor.Id] : _original);
        device.PollIntervalMs = IntegerValue(_cycle); device.TimeoutMs = IntegerValue(_timeout);
        return device;
    }

    private T Register<T>(string key, T input) where T : Control
    {
        input.Name = "DeviceConfiguration_" + key;
        _fields.Add(key, input);
        return input;
    }

    private static int IntegerValue(NumericUpDown input)
    {
        if (input.Value != decimal.Truncate(input.Value)) throw new InvalidOperationException("配置数值必须为整数。");
        return checked((int)input.Value);
    }

    private static TabPage TabOf(TableLayoutPanel table)
    {
        for (Control? parent = table.Parent; parent is not null; parent = parent.Parent)
            if (parent is TabPage tab) return tab;
        throw new InvalidOperationException("设备字段未挂入页签。");
    }
    private sealed record ChannelOption(Guid? Id, string Name) { public override string ToString() => Name; }
    private static DeviceConfiguration Clone(DeviceConfiguration value) => GatewayJson.Clone(value);
}
