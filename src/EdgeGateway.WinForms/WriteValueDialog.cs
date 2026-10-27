using EdgeGateway.Model;
using EdgeGateway.Application;
using System.Globalization;

namespace EdgeGateway.WinForms;

internal sealed partial class WriteValueDialog : DpiAwareForm
{
    private readonly TagDataType _type;
    private bool _arranging;
    private string? _acceptedValue;
    public string Value => _acceptedValue ?? (TagEditor.IsStringType(_type) ? _value.Text : _value.Text.Trim());
    public WriteVerificationOptions VerificationOptions { get; private set; } = new();
    internal IReadOnlyList<Control> SmokeCommands => new Control[] { _confirm, _cancel };
    internal Control SmokeValueInput => _value;
    internal IReadOnlyList<Control> SmokeVerificationInputs => [_verify, _verificationTimeout, _absoluteTolerance, _relativeTolerance];

    public WriteValueDialog() { InitializeComponent(); }

    public WriteValueDialog(string device, string tag, string unit, string current, TagDataType type)
    {
        _type = type;
        Text = "输入写入值"; Font = new Font("Microsoft YaHei UI", 10F); StartPosition = FormStartPosition.CenterParent;
        ConfigureWindow(new Size(604, 560), new Size(464, 270));
        BackColor = UiTheme.Background; ForeColor = UiTheme.Ink;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        InitializeComponent();
        targetLabel.Text = device + " / " + tag;
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
        valueHelp.Text = help;
        _value.Text = current;
        var multiline = TagEditor.IsStringType(type) || type == TagDataType.Any;
        _value.Multiline = multiline; _value.ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None;
        _value.Height = multiline ? 120 : 38;
        _valueSurface.MinimumSize = new Size(0, multiline ? 120 : 38);
        _confirm.Click += (_, _) =>
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
        };

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
