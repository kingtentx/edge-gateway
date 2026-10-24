using EdgeGateway.Application;
using EdgeGateway.Model;
using System.Globalization;

namespace EdgeGateway.WinForms;

/// <summary>Creates or edits every device setting through one isolated, driver-aware draft.</summary>
internal sealed class DeviceConfigurationDialog : EditorDialog
{
    private readonly DeviceConfiguration _original;
    private readonly IDriverCatalog _drivers;
    private readonly bool _readOnly, _isNew;
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);
    private readonly List<Control> _connectionInputs = [];
    private readonly List<string> _connectionKeys = [];
    private readonly Dictionary<string, DeviceConfiguration> _driverDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _name, _group;
    private readonly ComboBox _driver, _channel, _scanMode;
    private readonly CheckBox _enabled, _autoDemotion;
    private readonly NumericUpDown _cycle, _timeout, _demotionCount, _demotionInterval;
    private readonly Label _driverDetails;
    private DriverDescriptor? _descriptor;
    private DriverParameterPanel? _parameters;
    private TabPage? _protocolPage;

    /// <summary>Available only after a successful commit; never aliases the caller's device or tags.</summary>
    public DeviceConfiguration? Value { get; private set; }
    internal IReadOnlyCollection<Control> SmokeParameterInputs => _parameters?.Inputs ?? _connectionInputs;
    internal Guid? SmokeChannelId => (_channel.SelectedItem as ChannelOption)?.Id;

    public DeviceConfigurationDialog(DeviceConfiguration device, IDriverCatalog drivers, bool readOnly = false, bool isNew = false,
        IReadOnlyList<ChannelConfiguration>? channels = null)
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

        var acquisition = AddTab("采集设置");
        TabOf(acquisition).Name = "AcquisitionSettings";
        Section(acquisition, "设备信息");
        _enabled = Register("Enabled", new CheckBox
        {
            Text = "启用设备", Checked = _original.Enabled, AutoSize = true,
            MinimumSize = new Size(0, 32)
        });
        _name = Register("Name", TextInput(_original.Name));
        _group = Register("Group", TextInput(_original.Group));
        _channel = Register("ChannelId", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
        EditorLayout.ConfigureDropDown(_channel, 520, 320);
        _channel.Items.Add(new ChannelOption(null, "独立通信"));
        _channel.Items.AddRange((channels ?? []).Select(c => new ChannelOption(c.Id, c.Name)).Cast<object>().ToArray());
        if (_original.ChannelId.HasValue && !_channel.Items.Cast<ChannelOption>().Any(c => c.Id == _original.ChannelId))
            _channel.Items.Add(new ChannelOption(_original.ChannelId, "现有通道 · " + _original.ChannelId));
        _channel.SelectedItem = _channel.Items.Cast<ChannelOption>().First(c => c.Id == _original.ChannelId);
        _driver = Register("DriverId", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FormattingEnabled = true });
        EditorLayout.ConfigureDropDown(_driver, 620, 440);
        _driver.Format += (_, e) => { if (e.ListItem is DriverDescriptor descriptor) e.Value = descriptor.Name + "  ·  " + descriptor.Id; };
        // Populate before selecting: an unbound catalog must also work before the form is shown.
        var catalog = _drivers.Drivers.ToArray();
        _driver.Items.AddRange(catalog.Cast<object>().ToArray());
        _driver.SelectedIndex = Array.FindIndex(catalog, x => string.Equals(x.Id, _original.DriverId, StringComparison.OrdinalIgnoreCase));
        if (_driver.SelectedIndex < 0) throw new InvalidOperationException("未找到设备使用的采集驱动：" + _original.DriverId);
        _cycle = Register("PollIntervalMs", Number(_original.PollIntervalMs, 100, 3_600_000));
        _timeout = Register("TimeoutMs", Number(_original.TimeoutMs, 100, 120_000));
        Field(acquisition, "启用状态", _enabled);
        Field(acquisition, "设备名称", _name);
        Field(acquisition, "设备分组", _group);
        Section(acquisition, "通信与采集", "选择驱动后，在对应连接页配置设备地址。");
        Field(acquisition, "采集驱动", _driver);
        Field(acquisition, "通信通道", _channel);
        Field(acquisition, "采集周期 (ms)", _cycle);
        Field(acquisition, "通信超时 (ms)", _timeout);
        Hint(acquisition, "启用设备后可在列表中启动采集；取消勾选后禁止启动。更换驱动不会自动转换已有点位，请核对点位地址与方法。");
        _driverDetails = EditorLayout.WrappingLabel("", 10, UiTheme.Muted);
        var detailsRow = acquisition.RowCount++;
        acquisition.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        acquisition.Controls.Add(_driverDetails, 0, detailsRow); acquisition.SetColumnSpan(_driverDetails, 2);
        var scanning = AddTab("扫描与恢复");
        Section(scanning, "扫描策略", "按设备统一扫描，或按各点位的周期扫描。");
        _scanMode = Register("ScanMode", SelectInput(Enum.GetValues<DeviceScanMode>().Cast<object>().ToArray(), _original.ScanMode));
        _scanMode.Format += (_, e) => e.Value = e.ListItem is DeviceScanMode.TagRate ? "按点位周期扫描" : "按设备周期扫描";
        _scanMode.FormattingEnabled = true;
        _autoDemotion = Register("AutoDemotionEnabled", new CheckBox { Text = "连续失败后降低重试频率", Checked = _original.AutoDemotionEnabled, AutoSize = true });
        _demotionCount = Register("DemotionFailureCount", Number(_original.DemotionFailureCount, 1, 30));
        _demotionInterval = Register("DemotionIntervalMs", Number(_original.DemotionIntervalMs, 100, 3_600_000));
        Field(scanning, "扫描模式", _scanMode);
        EditorLayout.FieldNote(scanning, "点位周期设为 0 时继承设备周期。");
        Section(scanning, "故障恢复", "仅在启用故障降速后，按以下阈值降低重试频率。");
        Field(scanning, "故障降速", _autoDemotion);
        Field(scanning, "连续失败次数", _demotionCount);
        Field(scanning, "降速重试间隔 (ms)", _demotionInterval);
        Hint(scanning, "按点位周期扫描时，点位周期 0 继承设备周期。失败设备释放通道后退避；达到阈值按指定间隔重试，读取恢复后自动恢复正常扫描。诊断和写入按请求执行。");
        _driver.SelectedIndexChanged += (_, _) => UpdateProtocolFields();
        _cycle.ValueChanged += (_, _) => _parameters?.RefreshBindings(BoundDevice());
        _timeout.ValueChanged += (_, _) => _parameters?.RefreshBindings(BoundDevice());
        UpdateProtocolFields();
        Tabs.SelectedTab = TabOf(acquisition);
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
            foreach (var key in _connectionKeys) _fields.Remove(key);
            _connectionKeys.Clear(); _connectionInputs.Clear();
            if (_protocolPage is not null)
            {
                Tabs.TabPages.Remove(_protocolPage);
                _protocolPage.Dispose();
                _protocolPage = null;
            }
            _parameters = null;
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
            if (descriptor.IsLegacy || descriptor.Parameters.Count > 0)
            {
                _parameters = new DriverParameterPanel();
                _parameters.SetReadOnly(_readOnly);
                _protocolPage = new TabPage("驱动参数") { Name = "DriverConfigurationParameters", BackColor = Color.White };
                _protocolPage.Controls.Add(_parameters);
                Tabs.TabPages.Add(_protocolPage);
                _parameters.LoadParameters(descriptor, BoundDevice(), draft.Properties, "采集设置");
            }
            else if (descriptor.Id is "modbus-tcp" or "modbus-rtu") BuildNativePage(descriptor.Id, draft);
            _driverDetails.Text = descriptor.Description
                + (string.IsNullOrWhiteSpace(descriptor.Prerequisites) ? "" : "\n运行条件：" + descriptor.Prerequisites)
                + (descriptor.RequiresSta ? "\n此驱动使用专用 STA 线程运行。" : "");
            ApplyReadOnlyState();
            Value = null;
        }
        finally { Tabs.ResumeLayout(true); }
    }

    private void BuildNativePage(string driverId, DeviceConfiguration draft)
    {
        // Build the complete 96-DPI subtree before attaching it to an already shown
        // TabControl. The viewport then scales only this new protocol page once.
        var viewport = new EditorLayout.FieldViewport();
        var connection = viewport.Table;
        var rtu = driverId == "modbus-rtu";
        _protocolPage = new TabPage(rtu ? "串口连接" : "网络连接") { Name = "NativeConnection", BackColor = Color.White };
        Section(connection, rtu ? "串口连接参数" : "网络连接参数", rtu ? "与设备端的串口设置保持一致。" : "填写设备实际使用的主机地址、端口与站号。");
        if (rtu)
        {
            AddConnection(connection, "SerialPortName", "串口名称", TextInput(draft.SerialPortName));
            AddConnection(connection, "BaudRate", "波特率", Number(draft.BaudRate, 300, 4_000_000));
            AddConnection(connection, "DataBits", "数据位", Number(draft.DataBits, 5, 8));
            AddConnection(connection, "Parity", "校验位", Choice(["None", "Odd", "Even", "Mark", "Space"], draft.Parity));
            AddConnection(connection, "StopBits", "停止位", Choice(["One", "Two"], draft.StopBits));
            AddConnection(connection, "UnitId", "Modbus 站号", Number(draft.UnitId, 1, 247));
            Hint(connection, "Modbus RTU 二进制通信要求 8 个数据位，停止位选择 One 或 Two；同一串口当前只允许一个启用设备。");
        }
        else
        {
            AddConnection(connection, "Host", "主机地址", TextInput(draft.Host));
            AddConnection(connection, "Port", "TCP 端口", Number(draft.Port, 1, 65535));
            AddConnection(connection, "UnitId", "Modbus 站号", Number(draft.UnitId, 1, 247));
            Hint(connection, "主机填写设备 IP 或域名；TCP 端口为 1–65535，站号为 1–247。寄存器地址、类型与字节序在点位中配置。");
        }
        _protocolPage.Controls.Add(viewport);
        Tabs.TabPages.Add(_protocolPage);
        viewport.CompleteInitialization();
        viewport.PerformAutoScale();
        viewport.PerformLayout();
    }

    private void ApplyReadOnlyState()
    {
        foreach (var input in _fields.Values)
        {
            if (input is TextBoxBase text) text.ReadOnly = _readOnly;
            else input.Enabled = !_readOnly;
        }
        _parameters?.SetReadOnly(_readOnly);
    }

    private DeviceConfiguration CaptureDriverDraft()
    {
        var descriptor = _descriptor ?? throw new InvalidOperationException("请选择采集驱动。");
        var candidate = Clone(_driverDrafts[descriptor.Id]);
        if (_parameters is not null) candidate.Properties = _parameters.CaptureProperties();
        else if (descriptor.Id == "modbus-tcp")
        {
            candidate.Host = _fields["Host"].Text.Trim();
            candidate.Port = IntegerValue((NumericUpDown)_fields["Port"]);
            candidate.UnitId = IntegerValue((NumericUpDown)_fields["UnitId"]);
        }
        else if (descriptor.Id == "modbus-rtu")
        {
            candidate.SerialPortName = _fields["SerialPortName"].Text.Trim();
            candidate.BaudRate = IntegerValue((NumericUpDown)_fields["BaudRate"]);
            candidate.DataBits = IntegerValue((NumericUpDown)_fields["DataBits"]);
            candidate.Parity = ((ComboBox)_fields["Parity"]).SelectedItem?.ToString() ?? "";
            candidate.StopBits = ((ComboBox)_fields["StopBits"]).SelectedItem?.ToString() ?? "";
            candidate.UnitId = IntegerValue((NumericUpDown)_fields["UnitId"]);
        }
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
        candidate.Group = Required(_group, "设备分组");
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
        var parameter = _parameters?.FindValidationInput(error);
        if (parameter is not null) return parameter;
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
        if (descriptor.IsLegacy && _connectionInputs.Count > 0)
            throw new InvalidOperationException("A migrated driver shows unrelated native connection fields.");
        _parameters?.VerifyBindingsForSmoke(descriptor, BoundDevice(), verifyInitialSelection: false);
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
        if (key == "DriverId") { SelectDriverForSmoke(value); return; }
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
        _connectionKeys.Add(key);
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
    private sealed record ChannelOption(Guid? Id, string Name) { public override string ToString() => Name; }
    private static DeviceConfiguration Clone(DeviceConfiguration value) => GatewayJson.Clone(value);
}
