using EdgeGateway.Model;
using System.Globalization;

namespace EdgeGateway.WinForms;

/// <summary>Edits reflected driver settings without asking the operator to write JSON.</summary>
internal sealed class DriverParameterPanel : UserControl
{
    private EditorLayout.FieldViewport? _page;
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly Dictionary<string, (DriverParameterDescriptor Metadata, Control Input)> _fields = new(StringComparer.Ordinal);
    private Dictionary<string, string> _original = [];
    private bool _readOnly;
    public IReadOnlyCollection<Control> Inputs => _fields.Values.Select(x => x.Input).ToArray();

    public DriverParameterPanel()
    {
        Dock = DockStyle.Fill; BackColor = Color.White; Margin = Padding.Empty;
        AutoScaleMode = AutoScaleMode.Inherit;
    }

    public void LoadParameters(DriverDescriptor descriptor, DeviceConfiguration device, Dictionary<string, string> properties,
        string basicTabName = "基本与采集")
    {
        SuspendLayout();
        if (_page is not null) { Controls.Remove(_page); _page.Dispose(); }
        _fields.Clear();
        // A displayed editor may already be at 144/192 DPI. New controls do not
        // inherit previously applied size/margin scaling from an existing table.
        // Give each replacement subtree its own 96-DPI baseline so WinForms scales
        // it once when attached to the current monitor; never re-scale its old host.
        _page = new EditorLayout.FieldViewport();
        var table = _page.Table;
        table.SuspendLayout();
        _original = new Dictionary<string, string>(properties, StringComparer.Ordinal);
        bool? previousBoundGroup = null;
        foreach (var parameter in descriptor.Parameters.OrderBy(parameter => parameter.BoundProperty is not null || parameter.IsReadOnly))
        {
            var bound = BoundValue(parameter, device);
            var boundGroup = parameter.BoundProperty is not null || parameter.IsReadOnly;
            if (previousBoundGroup != boundGroup)
            {
                EditorDialog.Section(table, boundGroup ? "自动关联参数" : "连接与驱动参数",
                    boundGroup ? $"下列参数只读；周期和超时请在“{basicTabName}”中修改。" : "按现场设备填写，参数选项与范围由驱动提供。");
                previousBoundGroup = boundGroup;
            }
            var value = bound ?? (properties.TryGetValue(parameter.Key, out var saved) ? saved : parameter.DefaultValue);
            var input = CreateInput(parameter, value);
            input.Name = "DriverParameter_" + parameter.Key;
            if (bound is not null || parameter.IsReadOnly)
            {
                if (input is TextBox text) text.ReadOnly = true;
                else input.Enabled = false;
            }
            var bindingHint = parameter.BoundProperty switch
            {
                "Id" => "自动使用设备唯一标识。", "TimeoutMs" => $"在“{basicTabName}”的通信超时中修改。",
                "PollIntervalMs" => $"在“{basicTabName}”的采集周期中修改。", _ => ""
            };
            var help = $"{parameter.Key} · {parameter.TypeName}\n{parameter.Description}\n{bindingHint}".Trim();
            _tips.SetToolTip(input, help);
            input.AccessibleDescription = help;
            EditorDialog.Field(table, parameter.Name + (bound is null ? "" : "（绑定）"), input);
            // A disabled enum/numeric field does not receive mouse events. Keep its
            // complete description available on the enabled caption in read-only views.
            if (table.GetControlFromPosition(0, table.RowCount - 1) is Control caption)
                _tips.SetToolTip(caption, help);
            if (!string.IsNullOrWhiteSpace(bindingHint)) EditorLayout.FieldNote(table, bindingHint);
            else if (!string.IsNullOrWhiteSpace(parameter.Description)) EditorLayout.FieldNote(table, parameter.Description);
            _fields.Add(parameter.Key, (parameter, input));
        }
        if (descriptor.Parameters.Count == 0) EditorDialog.Hint(table, "此驱动没有额外参数。");
        table.ResumeLayout(false);
        Controls.Add(_page);
        _page.CompleteInitialization();
        _page.PerformAutoScale();
        _page.PerformLayout();
        ResumeLayout(true);
        _page.AutoScrollPosition = Point.Empty;
        SetReadOnly(_readOnly);
    }

    public void SetReadOnly(bool readOnly)
    {
        _readOnly = readOnly;
        foreach (var field in _fields.Values)
        {
            var locked = readOnly || field.Metadata.IsReadOnly || field.Metadata.BoundProperty is not null;
            if (field.Input is TextBox text) text.ReadOnly = locked;
            else field.Input.Enabled = !locked;
        }
    }

    public void RefreshBindings(DeviceConfiguration device)
    {
        foreach (var field in _fields.Values)
        {
            var value = BoundValue(field.Metadata, device);
            if (value is not null && field.Input is TextBox text) text.Text = value;
        }
    }

    public Dictionary<string, string> CaptureProperties()
    {
        var result = new Dictionary<string, string>(_original, StringComparer.Ordinal);
        foreach (var field in _fields.Values)
        {
            if (field.Metadata.BoundProperty is not null) { result.Remove(field.Metadata.Key); continue; }
            result[field.Metadata.Key] = ReadInput(field.Input);
        }
        return result;
    }

    internal Control? FindValidationInput(string message)
    {
        foreach (var field in _fields.Values)
            if (message.Contains("参数 " + field.Metadata.Key, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(field.Metadata.Name) && message.Contains(field.Metadata.Name, StringComparison.OrdinalIgnoreCase)
                || field.Metadata.Key == "Uri" && message.Contains("URI", StringComparison.OrdinalIgnoreCase))
                return field.Input;
        return null;
    }

    public void VerifyBindingsForSmoke(DriverDescriptor descriptor, DeviceConfiguration device, bool verifyInitialSelection = true)
    {
        if (_fields.Count != descriptor.Parameters.Count) throw new InvalidOperationException($"{descriptor.Id}: missing reflected parameter controls.");
        var captured = CaptureProperties();
        foreach (var field in _fields.Values)
        {
            if (field.Metadata.Options.Count > 0 && field.Input is ComboBox combo)
            {
                var expected = _original.GetValueOrDefault(field.Metadata.Key, field.Metadata.DefaultValue);
                if (combo.SelectedIndex < 0 || verifyInitialSelection && combo.SelectedItem?.ToString() != expected)
                    throw new InvalidOperationException($"{descriptor.Id}: parameter {field.Metadata.Key} lost its configured selection.");
                foreach (var option in field.Metadata.Options)
                    if (!combo.Items.Contains(option)) throw new InvalidOperationException($"{descriptor.Id}: parameter {field.Metadata.Key} lost enum option {option}.");
            }
            var bound = BoundValue(field.Metadata, device);
            if (bound is not null)
            {
                if (ReadInput(field.Input) != bound || captured.ContainsKey(field.Metadata.Key)) throw new InvalidOperationException($"{descriptor.Id}: invalid bound parameter {field.Metadata.Key}.");
            }
            else if (!captured.TryGetValue(field.Metadata.Key, out var value) || value != ReadInput(field.Input))
                throw new InvalidOperationException($"{descriptor.Id}: parameter {field.Metadata.Key} does not round-trip.");
        }
    }

    private static string? BoundValue(DriverParameterDescriptor parameter, DeviceConfiguration device) => parameter.BoundProperty switch
    {
        "Id" => device.Id.ToString(), "TimeoutMs" => device.TimeoutMs.ToString(CultureInfo.InvariantCulture),
        "PollIntervalMs" => device.PollIntervalMs.ToString(CultureInfo.InvariantCulture), _ => null
    };

    private static Control CreateInput(DriverParameterDescriptor parameter, string value)
    {
        if (parameter.BoundProperty is not null || parameter.IsReadOnly)
            return new TextBox { Text = value, ReadOnly = true, UseSystemPasswordChar = parameter.IsSecret, BorderStyle = BorderStyle.FixedSingle };
        if (parameter.Options.Count > 0)
        {
            var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            EditorLayout.ConfigureDropDown(input);
            input.Items.AddRange(parameter.Options.Cast<object>().ToArray());
            // Preserve invalid imported values visibly so validation can report them; never silently substitute a different enum.
            if (!input.Items.Contains(value)) input.Items.Add(value);
            input.SelectedItem = value;
            return input;
        }
        var type = parameter.TypeName.Split('.').Last().TrimEnd('?').ToLowerInvariant();
        if (type is "boolean" or "bool" && (bool.TryParse(value, out _) || value is "0" or "1"))
            return new CheckBox { Text = "启用", Checked = value == "1" || bool.TryParse(value, out var flag) && flag, AutoSize = true, Height = 32 };
        var range = type switch
        {
            "byte" => ((decimal)byte.MinValue, (decimal)byte.MaxValue),
            "sbyte" => ((decimal)sbyte.MinValue, (decimal)sbyte.MaxValue),
            "short" or "int16" => ((decimal)short.MinValue, (decimal)short.MaxValue),
            "ushort" or "uint16" => ((decimal)ushort.MinValue, (decimal)ushort.MaxValue),
            "int" or "int32" => ((decimal)int.MinValue, (decimal)int.MaxValue),
            "uint" or "uint32" => ((decimal)uint.MinValue, (decimal)uint.MaxValue),
            "long" or "int64" => ((decimal)long.MinValue, (decimal)long.MaxValue),
            "ulong" or "uint64" => ((decimal)ulong.MinValue, (decimal)ulong.MaxValue),
            _ => ((decimal)0, (decimal)0)
        };
        if (range.Item2 > range.Item1 && decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= range.Item1 && number <= range.Item2)
            return new NumericUpDown { Minimum = range.Item1, Maximum = range.Item2, Value = number, ThousandsSeparator = true, DecimalPlaces = 0, BorderStyle = BorderStyle.FixedSingle };
        return new TextBox { Text = value, UseSystemPasswordChar = parameter.IsSecret, BorderStyle = BorderStyle.FixedSingle };
    }

    private static string ReadInput(Control input) => input switch
    {
        CheckBox toggle => toggle.Checked ? "true" : "false",
        NumericUpDown number => number.Value.ToString(CultureInfo.InvariantCulture),
        ComboBox choice => choice.SelectedItem?.ToString() ?? "",
        _ => input.Text
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
