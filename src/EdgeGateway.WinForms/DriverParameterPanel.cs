using EdgeGateway.Model;
using System.Globalization;

namespace EdgeGateway.WinForms;

/// <summary>Edits reflected driver settings without asking the operator to write JSON.</summary>
internal sealed class DriverParameterPanel : UserControl
{
    private readonly TableLayoutPanel _table;
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly Dictionary<string, (DriverParameterDescriptor Metadata, Control Input)> _fields = new(StringComparer.Ordinal);
    private Dictionary<string, string> _original = [];
    public IReadOnlyCollection<Control> Inputs => _fields.Values.Select(x => x.Input).ToArray();

    public DriverParameterPanel()
    {
        Dock = DockStyle.Fill; AutoScroll = true; BackColor = Color.White; Padding = new Padding(14);
        _table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 0, Padding = new Padding(8) };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_table);
    }

    public void LoadParameters(DriverDescriptor descriptor, DeviceConfiguration device, Dictionary<string, string> properties)
    {
        SuspendLayout(); _table.SuspendLayout();
        foreach (Control control in _table.Controls.Cast<Control>().ToArray()) control.Dispose();
        _table.Controls.Clear(); _table.RowStyles.Clear(); _table.RowCount = 0; _fields.Clear();
        _original = new Dictionary<string, string>(properties, StringComparer.Ordinal);
        EditorDialog.Hint(_table, "按驱动参数逐项配置。枚举与数值范围来自原驱动；设备标识、超时和最小通讯周期与基本配置保持一致。");
        foreach (var parameter in descriptor.Parameters)
        {
            var bound = BoundValue(parameter, device);
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
                "Id" => "自动使用设备唯一标识。", "TimeoutMs" => "在“基本与采集”的通信超时中修改。",
                "PollIntervalMs" => "在“基本与采集”的采集周期中修改。", _ => ""
            };
            _tips.SetToolTip(input, $"{parameter.Key} · {parameter.TypeName}\n{parameter.Description}\n{bindingHint}".Trim());
            EditorDialog.Field(_table, parameter.Name + (bound is null ? "" : "（绑定）"), input);
            _fields.Add(parameter.Key, (parameter, input));
        }
        if (descriptor.Parameters.Count == 0) EditorDialog.Hint(_table, "此驱动没有额外参数。");
        _table.ResumeLayout(true); ResumeLayout(true); AutoScrollPosition = Point.Empty;
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

    public void VerifyBindingsForSmoke(DriverDescriptor descriptor, DeviceConfiguration device)
    {
        if (_fields.Count != descriptor.Parameters.Count) throw new InvalidOperationException($"{descriptor.Id}: missing reflected parameter controls.");
        var captured = CaptureProperties();
        foreach (var field in _fields.Values)
        {
            if (field.Metadata.Options.Count > 0 && field.Input is ComboBox combo)
                foreach (var option in field.Metadata.Options)
                    if (!combo.Items.Contains(option)) throw new InvalidOperationException($"{descriptor.Id}: parameter {field.Metadata.Key} lost enum option {option}.");
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
            return new TextBox { Text = value, ReadOnly = true };
        if (parameter.Options.Count > 0)
        {
            var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, DropDownHeight = 320 };
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
            return new NumericUpDown { Minimum = range.Item1, Maximum = range.Item2, Value = number, ThousandsSeparator = true, DecimalPlaces = 0 };
        return new TextBox { Text = value, UseSystemPasswordChar = parameter.IsSecret };
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
