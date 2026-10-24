using EdgeGateway.Model;
using EdgeGateway.Application;
using System.Globalization;

namespace EdgeGateway.WinForms;

internal abstract class EditorDialog : DpiAwareForm
{
    protected readonly TabControl Tabs = new EditorLayout.EditorTabs() { Name = "EditorTabs", Dock = DockStyle.Fill, Multiline = true, Margin = Padding.Empty };
    private readonly Button _confirm, _cancel;
    private readonly TableLayoutPanel _layout, _heading, _titleRow;
    private readonly FlowLayoutPanel _buttons;
    private readonly Label _title, _description;
    private readonly LinkLabel _help;
    private readonly EditorFeedback _feedback = new("EditorValidation");
    private readonly List<EditorLayout.FieldViewport> _fieldViewports = [];
    private bool _arranging;
    private bool? _compactMode;
    private bool _readOnlyView;
    private bool _applying;
    private Func<Task>? _applyChanges;
    private Action<Exception>? _applyFailed;
    internal Exception? LastApplyErrorForSmoke { get; private set; }
    internal bool SuppressApplyErrorDialogForSmoke { get; set; }
    internal IReadOnlyList<TabPage> SmokeTabs => Tabs.TabPages.Cast<TabPage>().ToArray();
    internal TabPage? SelectedSmokeTab { get => Tabs.SelectedTab; set => Tabs.SelectedTab = value; }
    internal IReadOnlyList<Control> SmokeCommands => _readOnlyView ? new Control[] { _cancel } : new Control[] { _confirm, _cancel };
    protected EditorDialog(string title, string hint)
    {
        Text = title; Font = new Font("Microsoft YaHei UI", 10F); BackColor = UiTheme.Background;
        ForeColor = UiTheme.Ink; StartPosition = FormStartPosition.CenterParent;
        ConfigureWindow(new Size(724, 611), new Size(604, 481));
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Name = "EditorLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20), Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 14)
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // A spanning description in the title/help table can give its preferred width
        // to the AutoSize help column and squeeze the title down to one character.
        // Keep that text in its own full-width row, outside the two-column title row.
        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _title = EditorLayout.WrappingLabel(title, 20, UiTheme.Ink, true);
        _title.Name = "EditorHeadingTitle";
        titleRow.Controls.Add(_title, 0, 0);
        _description = EditorLayout.WrappingLabel(hint, 10, UiTheme.Muted);
        _description.Name = "EditorHeadingDescription";
        heading.Controls.Add(titleRow, 0, 0);
        heading.Controls.Add(_description, 0, 1);
        _help = new LinkLabel
        {
            Text = "查看说明", AutoSize = true, Visible = false, TabStop = true,
            LinkColor = UiTheme.Blue, ActiveLinkColor = UiTheme.Blue,
            LinkBehavior = LinkBehavior.HoverUnderline, Anchor = AnchorStyles.Right,
            Margin = new Padding(12, 8, 0, 0), AccessibleDescription = hint
        };
        _help.LinkClicked += (_, _) => MessageBox.Show(this, _description.Text, "配置说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        titleRow.Controls.Add(_help, 1, 0);
        var save = UiTheme.Button("保存配置", async (_, _) =>
        {
            if (_applying) return;
            try { await ApplyAndAcceptAsync(); }
            catch { /* The same apply path has already retained the draft and shown inline feedback. */ }
        }, true);
        save.Name = "EditorConfirm";
        var cancel = UiTheme.Button("取消"); cancel.Name = "EditorCancel"; cancel.DialogResult = DialogResult.Cancel;
        _confirm = save; _cancel = cancel;
        var buttons = UiTheme.Toolbar(save, cancel); buttons.FlowDirection = FlowDirection.RightToLeft; buttons.WrapContents = false;
        buttons.Name = "EditorActions"; buttons.BackColor = Color.White; buttons.Padding = new Padding(12, 14, 4, 4);
        buttons.Paint += (_, e) => { using var pen = new Pen(UiTheme.Border); e.Graphics.DrawLine(pen, 0, 0, buttons.Width, 0); };
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(Tabs, 0, 1); layout.Controls.Add(_feedback, 0, 2); layout.Controls.Add(buttons, 0, 3);
        _layout = layout; _heading = heading; _titleRow = titleRow; _buttons = buttons;
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }

    internal void VerifyHeadingForSmoke(string context)
    {
        var bounds = _title.RectangleToScreen(_title.ClientRectangle);
        if (!_title.Visible || bounds.Width <= 0 || bounds.Height <= 0
            || !_heading.RectangleToScreen(_heading.ClientRectangle).Contains(bounds)
            || !RectangleToScreen(ClientRectangle).Contains(bounds))
            throw new InvalidOperationException($"{context}: editor heading has no fully visible area ({bounds}).");

        var singleLine = _title.GetPreferredSize(new Size(int.MaxValue, int.MaxValue));
        var availableWidth = _heading.ClientSize.Width - _heading.Padding.Horizontal
            - _titleRow.Margin.Horizontal - _titleRow.Padding.Horizontal - _title.Margin.Horizontal
            - (_help.Visible ? _help.Width + _help.Margin.Horizontal : 0);
        var tolerance = UiTheme.Scale(this, 2);
        if (availableWidth >= singleLine.Width
            && (_title.ClientSize.Width + tolerance < singleLine.Width
                || Math.Abs(_title.ClientSize.Height - singleLine.Height) > tolerance))
            throw new InvalidOperationException($"{context}: heading '{_title.Text}' wraps or is clipped despite available width {availableWidth}; bounds {_title.Bounds}, single-line preferred size {singleLine}.");
    }

    protected override void OnLoad(EventArgs e)
    {
        foreach (var viewport in _fieldViewports) viewport.CompleteInitialization();
        base.OnLoad(e);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_layout is null || _arranging || !IsHandleCreated || !IsDpiLayoutReady) return;
        _arranging = true;
        try
        {
            var compact = ClientSize.Height < UiTheme.Scale(this, 460);
            var padding = UiTheme.Scale(this, new Padding(compact ? 12 : 20));
            if (_layout.Padding != padding) _layout.Padding = padding;
            var headingPadding = UiTheme.Scale(this, new Padding(0, 0, 0, compact ? 6 : 14));
            if (_heading.Padding != headingPadding) _heading.Padding = headingPadding;
            var buttonPadding = UiTheme.Scale(this, new Padding(12, compact ? 8 : 14, 4, 4));
            if (_buttons.Padding != buttonPadding) _buttons.Padding = buttonPadding;
            if (_compactMode != compact)
            {
                _compactMode = compact;
                _description.Visible = !compact;
                _help.Visible = compact;
            }
        }
        finally { _arranging = false; }
    }

    protected abstract void Commit();
    protected virtual Control? ResolveValidationField(Exception exception) => (exception as EditorValidationException)?.Field;

    protected void SetConfirmText(string text) => _confirm.Text = text;
    protected void ClearValidationFeedback() => _feedback.Clear();

    /// <summary>Validate and persist while the editor is still open, so any failure retains the user's inputs.</summary>
    internal void SetApplyHandler(Func<Task> apply, bool runningDevice, Action<Exception> onFailure)
    {
        _applyChanges = apply;
        _applyFailed = onFailure;
        if (!runningDevice) return;
        _confirm.Text = "保存并应用";
        _description.Text = "该设备正在采集。修改后保存将仅重新连接此设备；禁用设备将停止它。其他设备继续采集。";
        _help.AccessibleDescription = _description.Text;
    }

    private async Task ApplyAndAcceptAsync()
    {
        _applying = true;
        _confirm.Enabled = _cancel.Enabled = Tabs.Enabled = false;
        UseWaitCursor = true;
        LastApplyErrorForSmoke = null;
        _feedback.Clear();
        var confirmText = _confirm.Text;
        _confirm.Text = "正在保存…";
        try
        {
            try
            {
                Commit();
                if (_applyChanges is not null) await _applyChanges();
            }
            finally
            {
                _applying = false;
                _confirm.Enabled = !_readOnlyView;
                _cancel.Enabled = Tabs.Enabled = true;
                _confirm.Text = confirmText;
                UseWaitCursor = false;
            }
        }
        catch (Exception exception)
        {
            LastApplyErrorForSmoke = exception;
            _feedback.ShowFailure(exception, ResolveValidationField(exception));
            _applyFailed?.Invoke(exception);
            throw;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    internal Task ApplyAndAcceptForSmokeAsync() => ApplyAndAcceptAsync();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_applying) e.Cancel = true;
        base.OnFormClosing(e);
    }

    /// <summary>Keep a configuration view navigable while removing every apply action.</summary>
    protected void SetReadOnlyView()
    {
        _readOnlyView = true;
        _confirm.Enabled = false;
        _confirm.Visible = false;
        _cancel.Text = "关闭";
        AcceptButton = _cancel;
        CancelButton = _cancel;
    }

    protected TableLayoutPanel AddTab(string title)
    {
        var page = new TabPage(title) { BackColor = Color.White };
        var viewport = new EditorLayout.FieldViewport();
        _fieldViewports.Add(viewport);
        page.Controls.Add(viewport); Tabs.TabPages.Add(page); return viewport.Table;
    }

    internal static void Field(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = EditorLayout.WrappingLabel(label, 10, UiTheme.Ink); caption.Padding = new Padding(0, 7, 14, 7);
        caption.Margin = Padding.Empty;
        control.Margin = new Padding(0, 5, 0, 10);
        control.AccessibleName ??= label;
        control.TabIndex = row;
        UiTheme.StyleInput(control);
        if (control is not CheckBox) control.Dock = DockStyle.Fill;
        table.Controls.Add(caption, 0, row); table.Controls.Add(control, 1, row);
    }
    internal static void Section(TableLayoutPanel table, string title, string? description = null) =>
        EditorLayout.Section(table, title, description);
    internal static void Hint(TableLayoutPanel table, string text)
    {
        var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = EditorLayout.WrappingLabel(text, 10, UiTheme.Muted);
        label.Padding = new Padding(12, 10, 12, 10); label.Margin = new Padding(0, 8, 0, 8); label.BackColor = UiTheme.Background;
        table.Controls.Add(label, 0, row); table.SetColumnSpan(label, 2);
    }
    protected static TextBox TextInput(string value) => new() { Text = value, MinimumSize = new Size(150, 30), BorderStyle = BorderStyle.FixedSingle };
    protected static NumericUpDown NumberInput(int value, int minimum, int maximum) => new() { Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), ThousandsSeparator = true, BorderStyle = BorderStyle.FixedSingle };
    protected static ComboBox SelectInput(object[] values, object? selected)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; box.Items.AddRange(values); box.SelectedItem = selected;
        EditorLayout.ConfigureDropDown(box);
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        return box;
    }
    protected static string Required(TextBox box, string label)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) throw new EditorValidationException(label + "不能为空。", box);
        return box.Text.Trim();
    }
}


internal sealed class TagEditor : EditorDialog
{
    public TagConfiguration Value { get; }
    private readonly DriverDescriptor _driver;
    private readonly TextBox _name, _address, _unit, _scale, _offset, _low, _high, _description, _expression, _deadband;
    private readonly NumericUpDown _scanInterval;
    private readonly ComboBox _type, _area, _byteOrder, _readMethod, _writeMethod, _endian;
    private readonly CheckBox _enabled, _uploadEnabled, _writable;
    private readonly Label _methodHint;
    private readonly TableLayoutPanel _basic;
    private DriverMethodDescriptor? _priorMethod;
    private bool _initializing = true;

    public TagEditor(TagConfiguration value, DriverDescriptor driver)
        : base("采集点位", "定义地址与数据类型，再配置换算、报警和上报规则。")
    {
        Value = value; _driver = driver;
        _basic = AddTab("方法与地址");
        Section(_basic, "采集地址");
        _name = TextInput(value.Name); _address = TextInput(value.Address);
        _name.Name = "TagName"; _address.Name = "TagAddress";
        _readMethod = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        EditorLayout.ConfigureDropDown(_readMethod, 680, 360);
        _readMethod.Items.AddRange(driver.Methods.Select(x => new MethodChoice(x)).Cast<object>().ToArray());
        var selectedName = string.IsNullOrEmpty(value.ReadMethod) ? driver.DefaultReadMethod : value.ReadMethod;
        var selectedMethod = driver.Methods.FirstOrDefault(x => x.Name == selectedName) ?? driver.Methods.FirstOrDefault(x => !x.IsDiagnostic);
        if (selectedMethod is not null) _readMethod.SelectedItem = _readMethod.Items.Cast<MethodChoice>().First(x => x.Method.Name == selectedMethod.Name);
        if (!string.IsNullOrEmpty(value.ReadMethod) && !driver.Methods.Any(x => x.Name == value.ReadMethod) && driver.IsLegacy)
        {
            var unknown = new MethodChoice(new DriverMethodDescriptor(value.ReadMethod, "未找到原方法", "请选择当前驱动提供的实际方法。"));
            _readMethod.Items.Add(unknown); _readMethod.SelectedItem = unknown;
        }
        _type = SelectInput(SupportedTypes(driver, selectedMethod).Cast<object>().ToArray(), value.DataType);
        _area = SelectInput(Enum.GetValues<ModbusArea>().Cast<object>().ToArray(), value.Area);
        _byteOrder = SelectInput(Enum.GetValues<RegisterByteOrder>().Cast<object>().ToArray(), value.ByteOrder);
        _endian = SelectInput(Enum.GetValues<ProtocolEndian>().Cast<object>().ToArray(), value.Endian);
        _unit = TextInput(value.Unit);
        _unit.Name = "TagUnit";
        _enabled = new CheckBox { Text = "启用采集", Checked = value.Enabled, AutoSize = true };
        _uploadEnabled = new CheckBox { Name = "TagUploadEnabled", Text = "允许上传到平台", Checked = value.UploadEnabled, AutoSize = true };
        _writable = new CheckBox { Text = driver.SupportsWrite ? "允许手动写入此点位" : "当前驱动不提供写入能力", Checked = value.Writable && driver.SupportsWrite, Enabled = driver.SupportsWrite, AutoSize = true };
        _writeMethod = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        EditorLayout.ConfigureDropDown(_writeMethod, 680, 280);
        _writeMethod.Items.Add(new MethodChoice(new DriverMethodDescriptor("", "使用读取方法", "")));
        _writeMethod.Items.AddRange(driver.Methods.Where(x => x.SupportsWrite).Select(x => new MethodChoice(x)).Cast<object>().ToArray());
        _writeMethod.SelectedItem = _writeMethod.Items.Cast<MethodChoice>().FirstOrDefault(x => x.Method.Name == value.WriteMethod) ?? _writeMethod.Items[0];
        _writeMethod.Enabled = driver.SupportsWrite;
        Field(_basic, "点位名称", _name);
        if (driver.Methods.Count > 0) Field(_basic, "读取方法", _readMethod);
        Field(_basic, "点位地址", _address); Field(_basic, "数据类型", _type);
        if (driver.IsLegacy) Field(_basic, "协议字节序", _endian);
        else if (driver.Id != "simulator") { Field(_basic, "Modbus 寄存区", _area); Field(_basic, "字节序", _byteOrder); }
        Field(_basic, "单位", _unit);
        Section(_basic, "采集与权限", "采集、平台上传和写入许可分别控制。");
        Field(_basic, "启用状态", _enabled); Field(_basic, "上传平台", _uploadEnabled); Field(_basic, "写入权限", _writable);
        Hint(_basic, "关闭上传后仍采集、显示，并按日志与数据库配置记录；已入队消息继续补传。");
        if (driver.IsLegacy && driver.SupportsWrite) Field(_basic, "写入方法", _writeMethod);
        _methodHint = EditorLayout.WrappingLabel("", 10, UiTheme.Muted);
        var hintRow = _basic.RowCount++; _basic.Controls.Add(_methodHint, 0, hintRow); _basic.SetColumnSpan(_methodHint, 2);
        var calculation = AddTab("换算与报警");
        Section(calculation, "工程值换算", "原始值 × 倍率 + 偏移量，再执行可选表达式。");
        _scale = TextInput(value.Scale.ToString("G17", CultureInfo.InvariantCulture)); _offset = TextInput(value.Offset.ToString("G17", CultureInfo.InvariantCulture));
        _low = TextInput(value.LowLimit?.ToString("G17", CultureInfo.InvariantCulture) ?? ""); _high = TextInput(value.HighLimit?.ToString("G17", CultureInfo.InvariantCulture) ?? "");
        _description = new TextBox { Text = value.Description, Multiline = true, Height = 100, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle };
        _expression = TextInput(value.Expression); _expression.Name = "TagExpression"; _expression.MaxLength = 512;
        _scale.Name = "TagScale"; _offset.Name = "TagOffset"; _low.Name = "TagLowLimit"; _high.Name = "TagHighLimit";
        Field(calculation, "倍率", _scale); Field(calculation, "偏移量", _offset); Field(calculation, "计算表达式（可空）", _expression);
        var previewInput = TextInput("1"); previewInput.Name = "ExpressionPreviewInput";
        var previewResult = EditorLayout.WrappingLabel("x 为倍率和偏移换算后的工程值。", 10, UiTheme.Muted);
        previewResult.Name = "ExpressionPreviewResult";
        Section(calculation, "表达式试算", "试算不会写入设备或更改实时采样。");
        Field(calculation, "试算 x", previewInput);
        Field(calculation, "表达式试算", UiTheme.Button("预览表达式", (_, _) =>
        {
            var result = NumericExpression.Preview(_expression.Text, previewInput.Text);
            previewResult.Text = result.Success ? "计算结果：" + result.Value?.ToString(CultureInfo.InvariantCulture) : "无法计算：" + result.Error;
            previewResult.ForeColor = result.Success ? UiTheme.Ink : Color.Firebrick;
        }));
        Field(calculation, "试算结果", previewResult);
        Section(calculation, "阈值与说明");
        Field(calculation, "报警下限（可空）", _low); Field(calculation, "报警上限（可空）", _high); Field(calculation, "点位说明", _description);
        Hint(calculation, "数值使用英文小数点。字符串、时间、布尔和 Any 类型不参与倍率换算或数值报警。64 位整数不换算时保留完整整数精度。");
        Hint(calculation, "表达式支持 x、+ - * / % ^、abs/min/max/clamp/round/floor/ceil/truncate/pow；例如 clamp(x,0,100)。表达式点必须关闭写入权限；除零或越界将标记为 Bad，不沿用旧值。");
        var reporting = AddTab("扫描与上报");
        Section(reporting, "扫描与上报策略");
        _scanInterval = NumberInput(value.ScanIntervalMs, 0, 3_600_000); _scanInterval.Name = "TagScanInterval";
        _deadband = TextInput(value.Deadband.ToString("R", CultureInfo.InvariantCulture)); _deadband.Name = "TagDeadband";
        Field(reporting, "点位扫描周期 (ms)", _scanInterval);
        Field(reporting, "变化上报绝对死区", _deadband);
        Hint(reporting, "周期 0 继承设备周期，其他值为 100–3600000 ms，仅在设备选择“按点位周期扫描”时生效。死区使用最终工程值；在平台选择变化上报后生效，质量变化和心跳不受死区抑制。");
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

internal sealed class WriteValueDialog : DpiAwareForm
{
    private readonly TextBox _value;
    private readonly CheckBox _verify;
    private readonly NumericUpDown _verificationTimeout;
    private readonly TextBox _absoluteTolerance, _relativeTolerance;
    private readonly TagDataType _type;
    private readonly Button _confirm, _cancel;
    private readonly TableLayoutPanel _layout;
    private readonly Panel _viewport;
    private readonly FlowLayoutPanel _actions;
    private readonly Label _description;
    private readonly EditorFeedback _feedback = new("WriteValidation");
    private bool _arranging;
    private string? _acceptedValue;
    public string Value => _acceptedValue ?? (TagEditor.IsStringType(_type) ? _value.Text : _value.Text.Trim());
    public WriteVerificationOptions VerificationOptions { get; private set; } = new();
    internal IReadOnlyList<Control> SmokeCommands => new Control[] { _confirm, _cancel };
    internal Control SmokeValueInput => _value;
    internal IReadOnlyList<Control> SmokeVerificationInputs => [_verify, _verificationTimeout, _absoluteTolerance, _relativeTolerance];

    public WriteValueDialog(string device, string tag, string unit, string current, TagDataType type)
    {
        _type = type;
        Text = "输入写入值"; Font = new Font("Microsoft YaHei UI", 10F); StartPosition = FormStartPosition.CenterParent;
        ConfigureWindow(new Size(604, 560), new Size(464, 270));
        BackColor = UiTheme.Background; ForeColor = UiTheme.Ink;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        var panel = new TableLayoutPanel { Name = "WriteLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20), Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 10) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize)); heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        heading.Controls.Add(EditorLayout.WrappingLabel("写入点位", 20, UiTheme.Ink, true), 0, 0);
        _description = EditorLayout.WrappingLabel("输入工程值，下一步核对目标并确认写入。", 10, UiTheme.Muted);
        heading.Controls.Add(_description, 0, 1);
        panel.Controls.Add(heading, 0, 0);

        // Scroll only the content. Confirmation and cancellation remain reachable.
        var viewport = new Panel { Name = "WriteViewport", Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(10), Margin = Padding.Empty };
        var content = EditorLayout.Fields();
        EditorDialog.Section(content, "写入目标", device + " / " + tag);
        var help = type switch
        {
            TagDataType.Boolean => "输入 true / false 或 1 / 0。",
            TagDataType.Bit => "输入工程值，反算后的位值必须为 0 或 1；未设置换算时也可输入 true / false。",
            TagDataType.Int64 or TagDataType.UInt64 => "输入完整精度的工程值，反算后的原始值必须是范围内的 64 位整数。",
            TagDataType.DateTime => "输入 ISO 8601 时间，例如 2026-10-22T08:30:00+08:00。",
            TagDataType.Any => "输入由该方法支持的值；结构化值使用合法 JSON。",
            _ when TagEditor.IsStringType(type) => "输入完整文本，保留前后空格；允许写入空字符串。",
            _ => "输入新的工程值，单位：" + (string.IsNullOrEmpty(unit) ? "无" : unit)
        };
        var multiline = TagEditor.IsStringType(type) || type == TagDataType.Any;
        _value = new TextBox
        {
            Name = "WriteValue", Text = current, Multiline = multiline,
            Height = multiline ? 120 : 32, MinimumSize = new Size(0, multiline ? 36 : 32),
            BorderStyle = BorderStyle.FixedSingle, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
        };
        EditorDialog.Field(content, "新的工程值", _value);
        EditorLayout.FieldNote(content, help);
        EditorDialog.Section(content, "读回核对", "写入只提交一次，后续核对不会重复下发写入。");
        _verify = new CheckBox { Name = "WriteVerifyReadBack", Text = "写入后立即读回核对", Checked = true, AutoSize = true, MinimumSize = new Size(0, 32) };
        _verificationTimeout = new NumericUpDown { Name = "WriteVerificationTimeout", Minimum = 50, Maximum = 60000, Value = 5000 };
        _absoluteTolerance = new TextBox { Name = "WriteAbsoluteTolerance", Text = "0", MaxLength = 80 };
        _relativeTolerance = new TextBox { Name = "WriteRelativeTolerance", Text = "0", MaxLength = 80 };
        EditorDialog.Field(content, "核对开关", _verify);
        EditorDialog.Field(content, "总超时 (ms)", _verificationTimeout);
        EditorLayout.FieldNote(content, "包含写入与主动读回的时间。");
        EditorDialog.Field(content, "绝对容差", _absoluteTolerance);
        EditorLayout.FieldNote(content, "使用工程单位；0 表示精确匹配。");
        EditorDialog.Field(content, "相对容差", _relativeTolerance);
        EditorLayout.FieldNote(content, "范围 0–1，例如 0.01 表示 1%。");
        EditorDialog.Hint(content, "最多核对 5 次，间隔 200 ms。布尔和文本按实际值精确比较；读回不匹配或超时不会自动重复写入。");
        viewport.Controls.Add(content); panel.Controls.Add(viewport, 0, 1);
        panel.Controls.Add(_feedback, 0, 2);

        var ok = UiTheme.Button("下一步", (_, _) =>
        {
            _feedback.Clear();
            var value = Value;
            if (!TagEditor.IsStringType(type) && string.IsNullOrWhiteSpace(value))
            {
                _feedback.ShowFailure(new EditorValidationException("请输入要写入的工程值。", _value));
                return;
            }
            if (!double.TryParse(_absoluteTolerance.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var absolute) || !double.IsFinite(absolute) || absolute < 0)
            {
                _feedback.ShowFailure(new EditorValidationException("绝对容差须为非负有限数值。", _absoluteTolerance));
                return;
            }
            if (!double.TryParse(_relativeTolerance.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var relative) || !double.IsFinite(relative) || relative is < 0 or > 1)
            {
                _feedback.ShowFailure(new EditorValidationException("相对容差须为 0–1。", _relativeTolerance));
                return;
            }
            // Modeless Close disposes controls; preserve the accepted command independently of HWND lifetime.
            _acceptedValue = value;
            VerificationOptions = new() { VerifyReadBack = _verify.Checked, TimeoutMs = (int)_verificationTimeout.Value, AbsoluteTolerance = absolute, RelativeTolerance = relative };
            DialogResult = DialogResult.OK; Close();
        }, true);
        ok.Name = "WriteConfirm";
        var cancel = UiTheme.Button("取消"); cancel.Name = "WriteCancel"; cancel.DialogResult = DialogResult.Cancel;
        _confirm = ok; _cancel = cancel;
        var actions = UiTheme.Toolbar(ok, cancel); actions.FlowDirection = FlowDirection.RightToLeft; actions.WrapContents = false;
        actions.Name = "WriteActions"; actions.BackColor = Color.White; actions.Padding = new Padding(12, 14, 4, 4);
        actions.Paint += (_, e) => { using var pen = new Pen(UiTheme.Border); e.Graphics.DrawLine(pen, 0, 0, actions.Width, 0); };
        panel.Controls.Add(actions, 0, 3);
        _layout = panel; _viewport = viewport; _actions = actions;
        Controls.Add(panel); AcceptButton = ok; CancelButton = cancel;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_layout is null || _arranging || !IsHandleCreated || !IsDpiLayoutReady) return;
        _arranging = true;
        try
        {
            var compact = ClientSize.Height < UiTheme.Scale(this, 420);
            var padding = UiTheme.Scale(this, new Padding(compact ? 12 : 20));
            if (_layout.Padding != padding) _layout.Padding = padding;
            _description.Visible = !compact;
            var viewportPadding = UiTheme.Scale(this, new Padding(compact ? 6 : 10));
            if (_viewport.Padding != viewportPadding) _viewport.Padding = viewportPadding;
            var actionsPadding = UiTheme.Scale(this, new Padding(12, compact ? 8 : 14, 4, 4));
            if (_actions.Padding != actionsPadding) _actions.Padding = actionsPadding;
            if (_value.Multiline)
            {
                var available = _viewport.ClientSize.Height - _viewport.Padding.Vertical - _value.Margin.Vertical - UiTheme.Scale(this, 24);
                var height = Math.Max(UiTheme.Scale(this, 36), Math.Min(UiTheme.Scale(this, 120), available));
                if (_value.Height != height) _value.Height = height;
            }
        }
        finally { _arranging = false; }
    }
}
