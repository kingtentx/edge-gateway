using EdgeGateway.Model;
using System.Globalization;
using System.Text.Json;

namespace EdgeGateway.WinForms;

internal abstract class EditorDialog : Form
{
    protected readonly TabControl Tabs = new() { Dock = DockStyle.Fill, Padding = new Point(16, 8) };
    protected EditorDialog(string title, string hint)
    {
        Text = title; Font = new Font("Microsoft YaHei UI", 10F); BackColor = UiTheme.Background;
        ForeColor = UiTheme.Ink; StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(740, 650); MinimumSize = new Size(620, 520);
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 0, 0, 10) };
        heading.Controls.Add(UiTheme.Label(title, 18, UiTheme.Ink, true));
        var description = UiTheme.Label(hint, 9, UiTheme.Muted); description.MaximumSize = new Size(620, 0); heading.Controls.Add(description);
        var save = UiTheme.Button("确定", (_, _) =>
        {
            try { Commit(); DialogResult = DialogResult.OK; Close(); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "请检查输入", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }, true);
        var cancel = UiTheme.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
        var buttons = UiTheme.Toolbar(cancel, save); buttons.FlowDirection = FlowDirection.RightToLeft; buttons.Padding = new Padding(0, 15, 0, 0);
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(Tabs, 0, 1); layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }

    protected abstract void Commit();

    protected TableLayoutPanel AddTab(string title)
    {
        var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(14), AutoScroll = true };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 0, Padding = new Padding(8) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(table); Tabs.TabPages.Add(page); return table;
    }

    protected static void Field(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = UiTheme.Label(label, 10, UiTheme.Muted); caption.Padding = new Padding(0, 8, 10, 8);
        control.Margin = new Padding(0, 5, 0, 8);
        if (control is not CheckBox) control.Dock = DockStyle.Fill;
        table.Controls.Add(caption, 0, row); table.Controls.Add(control, 1, row);
    }
    protected static void Hint(TableLayoutPanel table, string text)
    {
        var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = UiTheme.Label(text, 9, UiTheme.Muted); label.MaximumSize = new Size(580, 0); label.Dock = DockStyle.Fill; label.Padding = new Padding(0, 10, 0, 5);
        table.Controls.Add(label, 0, row); table.SetColumnSpan(label, 2);
    }
    protected static TextBox TextInput(string value) => new() { Text = value, MinimumSize = new Size(150, 30) };
    protected static NumericUpDown NumberInput(int value, int minimum, int maximum) => new() { Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), ThousandsSeparator = true };
    protected static ComboBox SelectInput(object[] values, object? selected)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; box.Items.AddRange(values); box.SelectedItem = selected;
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        return box;
    }
    protected static string Required(TextBox box, string label)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) throw new InvalidOperationException(label + "不能为空。");
        return box.Text.Trim();
    }
}

internal sealed class DeviceEditor : EditorDialog
{
    public DeviceConfiguration Value { get; }
    private readonly bool _isNew;
    private readonly TextBox _name, _group, _host, _portName, _properties;
    private readonly NumericUpDown _cycle, _timeout, _port, _unit, _baud, _bits;
    private readonly ComboBox _driver, _parity, _stopBits;
    private readonly CheckBox _enabled;

    public DeviceEditor(DeviceConfiguration value, IReadOnlyList<DriverDescriptor> drivers, bool isNew = false)
        : base(isNew ? "添加采集设备" : "编辑采集设备", "设备连接与采集参数独立配置。点位地址、类型与字节序请根据设备协议确认。")
    {
        Value = value; _isNew = isNew;
        var basic = AddTab("基本与采集");
        _name = TextInput(value.Name); _group = TextInput(value.Group);
        _driver = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(DriverDescriptor.Name), DataSource = drivers.ToList() };
        _driver.SelectedItem = drivers.FirstOrDefault(x => x.Id == value.DriverId);
        _enabled = new CheckBox { Text = "启用此设备", Checked = value.Enabled, AutoSize = true, Height = 32 };
        _cycle = NumberInput(value.PollIntervalMs, 100, 3_600_000); _timeout = NumberInput(value.TimeoutMs, 100, 120_000);
        Field(basic, "设备名称", _name); Field(basic, "设备分组", _group); Field(basic, "采集驱动", _driver); Field(basic, "采集周期 (ms)", _cycle); Field(basic, "通信超时 (ms)", _timeout); Field(basic, "启用状态", _enabled);
        Hint(basic, "每台设备使用独立采集循环。点位读取按设备顺序执行，周期小于实际读取耗时时不会重叠执行。首次新建 Modbus 设备会带一个地址为 0 的示例点位。");
        var connection = AddTab("网络与串口");
        _host = TextInput(value.Host); _port = NumberInput(value.Port, 1, 65535); _unit = NumberInput(value.UnitId, 1, 247);
        _portName = TextInput(value.SerialPortName); _baud = NumberInput(value.BaudRate, 300, 4_000_000); _bits = NumberInput(value.DataBits, 5, 8);
        _parity = SelectInput(new object[] { "None", "Odd", "Even", "Mark", "Space" }, value.Parity);
        _stopBits = SelectInput(new object[] { "One", "Two", "OnePointFive" }, value.StopBits);
        Field(connection, "主机地址", _host); Field(connection, "TCP 端口", _port); Field(connection, "Modbus 站号", _unit);
        Field(connection, "串口名称", _portName); Field(connection, "波特率", _baud); Field(connection, "数据位", _bits); Field(connection, "校验位", _parity); Field(connection, "停止位", _stopBits);
        Hint(connection, "TCP 使用主机地址和端口。RTU 使用串口参数；当前一个串口只允许一个启用设备。模拟设备无需网络连接。");
        var advanced = AddTab("扩展参数");
        _properties = new TextBox { Text = JsonSerializer.Serialize(value.Properties, new JsonSerializerOptions { WriteIndented = true }), Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 210, AcceptsReturn = true, Font = new Font("Consolas", 10F) };
        Field(advanced, "驱动参数 JSON", _properties);
        Hint(advanced, "供自定义驱动读取的字符串键值字典。内置驱动使用前两个页签的参数即可，此处保留 {}。不要在此存放密码或令牌。");
        _driver.SelectedIndexChanged += (_, _) => UpdateProtocolFields(); UpdateProtocolFields();
    }

    private void UpdateProtocolFields()
    {
        var id = (_driver.SelectedItem as DriverDescriptor)?.Id ?? "";
        var rtu = id == "modbus-rtu"; var simulated = id == "simulator";
        _host.Enabled = _port.Enabled = !rtu && !simulated;
        _portName.Enabled = _baud.Enabled = _bits.Enabled = _parity.Enabled = _stopBits.Enabled = rtu;
        _unit.Enabled = id.StartsWith("modbus-", StringComparison.Ordinal);
    }

    protected override void Commit()
    {
        Value.Name = Required(_name, "设备名称"); Value.Group = Required(_group, "设备分组");
        var driver = _driver.SelectedItem as DriverDescriptor ?? throw new InvalidOperationException("请选择采集驱动。");
        Value.DriverId = driver.Id; Value.Enabled = _enabled.Checked;
        Value.PollIntervalMs = (int)_cycle.Value; Value.TimeoutMs = (int)_timeout.Value;
        Value.Host = _host.Text.Trim(); Value.Port = (int)_port.Value; Value.UnitId = (int)_unit.Value;
        Value.SerialPortName = _portName.Text.Trim(); Value.BaudRate = (int)_baud.Value; Value.DataBits = (int)_bits.Value;
        Value.Parity = _parity.Text; Value.StopBits = _stopBits.Text;
        Value.Properties = JsonSerializer.Deserialize<Dictionary<string, string>>(_properties.Text) ?? throw new InvalidOperationException("扩展参数必须是 JSON 对象。");
        if (Value.Properties.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value is null)) throw new InvalidOperationException("扩展参数必须使用非空名称与字符串值。");
        if (_isNew && Value.DriverId != "simulator")
        {
            Value.Tags = [new TagConfiguration { Name = "采集点位", Address = "0", DataType = TagDataType.UInt16 }];
        }
    }
}

internal sealed class TagEditor : EditorDialog
{
    public TagConfiguration Value { get; }
    private readonly TextBox _name, _address, _unit, _scale, _offset, _low, _high, _description;
    private readonly ComboBox _type, _area, _byteOrder;
    private readonly CheckBox _enabled, _writable;

    public TagEditor(TagConfiguration value, string driverId)
        : base("采集点位", "工程值 = 原始值 × 倍率 + 偏移量。写入时自动反算原始值，并按数据类型校验。")
    {
        Value = value;
        var basic = AddTab("地址与类型");
        _name = TextInput(value.Name); _address = TextInput(value.Address);
        _type = SelectInput(Enum.GetValues<TagDataType>().Cast<object>().ToArray(), value.DataType);
        _area = SelectInput(Enum.GetValues<ModbusArea>().Cast<object>().ToArray(), value.Area);
        _byteOrder = SelectInput(Enum.GetValues<RegisterByteOrder>().Cast<object>().ToArray(), value.ByteOrder);
        _unit = TextInput(value.Unit);
        _enabled = new CheckBox { Text = "启用采集", Checked = value.Enabled, AutoSize = true };
        _writable = new CheckBox { Text = "允许手动写入此点位", Checked = value.Writable, AutoSize = true };
        Field(basic, "点位名称", _name); Field(basic, "点位地址", _address); Field(basic, "数据类型", _type);
        Field(basic, "Modbus 寄存区", _area); Field(basic, "字节序", _byteOrder); Field(basic, "单位", _unit);
        Field(basic, "启用状态", _enabled); Field(basic, "写入权限", _writable);
        Hint(basic, driverId == "simulator"
            ? "模拟地址：sine、ramp、random、constant:数值、bool。模拟点位可用于验证采集、历史和上报链路。"
            : "Modbus 地址从 0 开始：手册中的 40001 通常填写 0，并选择 HoldingRegister。Coil / DiscreteInput 使用 Boolean。InputRegister / DiscreteInput 不可写。");
        _area.Enabled = _byteOrder.Enabled = driverId != "simulator";
        var calculation = AddTab("换算与报警");
        _scale = TextInput(value.Scale.ToString("G17", CultureInfo.InvariantCulture)); _offset = TextInput(value.Offset.ToString("G17", CultureInfo.InvariantCulture));
        _low = TextInput(value.LowLimit?.ToString("G17", CultureInfo.InvariantCulture) ?? ""); _high = TextInput(value.HighLimit?.ToString("G17", CultureInfo.InvariantCulture) ?? "");
        _description = new TextBox { Text = value.Description, Multiline = true, Height = 100, ScrollBars = ScrollBars.Vertical };
        Field(calculation, "倍率", _scale); Field(calculation, "偏移量", _offset); Field(calculation, "报警下限（可空）", _low); Field(calculation, "报警上限（可空）", _high); Field(calculation, "点位说明", _description);
        Hint(calculation, "使用英文小数点。报警阈值针对换算后的工程值，在越限 / 恢复时写入运行日志。留空表示不配置对应阈值。");
    }

    protected override void Commit()
    {
        Value.Name = Required(_name, "点位名称"); Value.Address = Required(_address, "点位地址"); Value.Unit = _unit.Text.Trim();
        Value.DataType = (TagDataType)_type.SelectedItem!; Value.Area = (ModbusArea)_area.SelectedItem!; Value.ByteOrder = (RegisterByteOrder)_byteOrder.SelectedItem!;
        Value.Enabled = _enabled.Checked; Value.Writable = _writable.Checked;
        Value.Scale = ParseNumber(_scale, "倍率"); Value.Offset = ParseNumber(_offset, "偏移量");
        Value.LowLimit = string.IsNullOrWhiteSpace(_low.Text) ? null : ParseNumber(_low, "报警下限");
        Value.HighLimit = string.IsNullOrWhiteSpace(_high.Text) ? null : ParseNumber(_high, "报警上限");
        if (Value.LowLimit.HasValue && Value.HighLimit.HasValue && Value.LowLimit.Value >= Value.HighLimit.Value) throw new InvalidOperationException("报警下限必须小于上限。");
        if (Value.Writable && Value.Scale == 0) throw new InvalidOperationException("允许写入时倍率不能为 0。");
        Value.Description = _description.Text.Trim();
    }

    private static double ParseNumber(TextBox input, string name)
    {
        if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw new InvalidOperationException(name + "必须为有效的有限数值。");
        return value;
    }
}

internal sealed class WriteValueDialog : Form
{
    private readonly TextBox _value;
    public string Value => _value.Text.Trim();
    public WriteValueDialog(string device, string tag, string unit, string current, bool boolean)
    {
        Text = "输入写入值"; Font = new Font("Microsoft YaHei UI", 10F); StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(520, 300); MinimumSize = new Size(480, 280); MaximizeBox = false; MinimizeBox = false;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24) };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(UiTheme.Label(device + " / " + tag, 12, UiTheme.Ink, true), 0, 0);
        panel.Controls.Add(UiTheme.Label(boolean ? "输入 true / false 或 1 / 0。" : "输入新的工程值，单位：" + (string.IsNullOrEmpty(unit) ? "无" : unit), 9, UiTheme.Muted), 0, 1);
        _value = new TextBox { Text = current == "—" ? "" : current, Dock = DockStyle.Top, Margin = new Padding(0, 15, 0, 10) }; panel.Controls.Add(_value, 0, 2);
        var ok = UiTheme.Button("下一步", (_, _) => { if (string.IsNullOrWhiteSpace(Value)) return; DialogResult = DialogResult.OK; Close(); }, true);
        var cancel = UiTheme.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
        var actions = UiTheme.Toolbar(cancel, ok); actions.FlowDirection = FlowDirection.RightToLeft; panel.Controls.Add(actions, 0, 3);
        Controls.Add(panel); AcceptButton = ok; CancelButton = cancel;
    }
}
