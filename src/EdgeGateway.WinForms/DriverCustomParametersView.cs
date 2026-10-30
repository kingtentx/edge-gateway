namespace EdgeGateway.WinForms;

internal sealed partial class DriverCustomParametersView : UserControl
{
    private const string DefaultHint = "自定义参数随保存配置一起提交；插件需读取同名参数才会生效。";
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);
    private bool _readOnly;
    private string? _selectedKey;
    private bool _binding;

    public DriverCustomParametersView()
    {
        InitializeComponent();
        keyInput.TextChanged += (_, _) => valueInput.UseSystemPasswordChar = IsSecret(keyInput.Text);
        grid.SelectionChanged += (_, _) =>
        {
            if (_binding) return;
            if (grid.CurrentRow?.DataBoundItem is not ParameterRow row) return;
            try { StorePending(); }
            catch (InvalidOperationException exception)
            {
                hint.Text = exception.Message;
                _binding = true;
                var previous = grid.Rows.FirstOrDefault(item => item.DataBoundItem is ParameterRow old && old.Key == _selectedKey);
                if (previous is not null) grid.CurrentCell = previous.Cells[0];
                _binding = false;
                return;
            }
            Rebind(_values.ContainsKey(row.Key) ? row.Key : _selectedKey);
            hint.Text = DefaultHint;
        };
        saveButton.Click += (_, _) =>
        {
            if (_readOnly) return;
            var key = keyInput.Text.Trim();
            if (key.Length == 0 || _reserved.Contains(key))
            { hint.Text = "请输入参数名；已声明的参数请在驱动参数页修改。"; keyInput.Focus(); return; }
            if (_values.ContainsKey(key) && key != _selectedKey)
            { hint.Text = "参数名已存在，请选择对应行后修改。"; return; }
            if (_selectedKey is not null && _selectedKey != key) _values.Remove(_selectedKey);
            _values[key] = valueInput.Text;
            Rebind(key);
            hint.Text = DefaultHint;
        };
        addButton.Click += (_, _) =>
        {
            try { StorePending(); }
            catch (InvalidOperationException exception) { hint.Text = exception.Message; return; }
            Rebind(); _selectedKey = null; keyInput.Text = valueInput.Text = ""; keyInput.Focus();
            hint.Text = DefaultHint;
        };
        removeButton.Click += (_, _) =>
        {
            if (_readOnly || grid.CurrentRow?.DataBoundItem is not ParameterRow row) return;
            _values.Remove(row.Key); _selectedKey = null; keyInput.Text = valueInput.Text = ""; Rebind();
            hint.Text = DefaultHint;
        };
    }

    public void LoadValues(IReadOnlyDictionary<string, string> values, IEnumerable<string> reserved, bool readOnly)
    {
        hint.Text = DefaultHint;
        _reserved.Clear(); _reserved.UnionWith(reserved);
        _selectedKey = null; keyInput.Text = valueInput.Text = "";
        _values.Clear();
        foreach (var pair in values.Where(pair => !_reserved.Contains(pair.Key))) _values.Add(pair.Key, pair.Value);
        _readOnly = readOnly;
        addButton.Enabled = saveButton.Enabled = removeButton.Enabled = !readOnly;
        keyInput.ReadOnly = valueInput.ReadOnly = readOnly;
        Rebind();
    }

    public void ApplyTo(Dictionary<string, string> properties)
    {
        StorePending();
        foreach (var key in properties.Keys.Where(key => !_reserved.Contains(key)).ToArray()) properties.Remove(key);
        foreach (var pair in _values) properties[pair.Key] = pair.Value;
    }

    private void StorePending()
    {
        var key = keyInput.Text.Trim();
        if (key.Length > 0 || valueInput.Text.Length > 0)
        {
            if (key.Length == 0 || _reserved.Contains(key) || _values.ContainsKey(key) && key != _selectedKey)
                throw new InvalidOperationException("自定义参数名称为空、重复或与驱动声明冲突，请先修正。");
            if (_selectedKey is not null && _selectedKey != key) _values.Remove(_selectedKey);
            _values[key] = valueInput.Text;
            _selectedKey = key;
        }
    }

    private void Rebind(string? key = null)
    {
        _binding = true;
        try
        {
            GridViewState.Rebind(grid, _values.Select(pair => new ParameterRow(pair.Key,
                IsSecret(pair.Key) && pair.Value.Length > 0 ? "••••••" : pair.Value)).ToArray(), row => row.Key, key);
            if (grid.CurrentRow?.DataBoundItem is ParameterRow row)
            { _selectedKey = row.Key; keyInput.Text = row.Key; valueInput.Text = _values[row.Key]; }
        }
        finally { _binding = false; }
    }
    private static bool IsSecret(string key) => new[] { "password", "token", "secret", "authorization", "密码", "授权" }
        .Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));
    private sealed record ParameterRow(string Key, string Value);

    internal void VerifyEditingForSmoke()
    {
        LoadValues(new Dictionary<string, string>(), ["Reserved"], false);
        keyInput.Text = "Endpoint"; valueInput.Text = "local://fixture"; saveButton.PerformClick();
        addButton.PerformClick(); keyInput.Text = "Token"; valueInput.Text = "fake-token"; saveButton.PerformClick();
        if (!valueInput.UseSystemPasswordChar || grid.Rows.Count != 2
            || grid.Rows[1].Cells["Value"].Value?.ToString() != "••••••") throw new InvalidOperationException("Custom parameter masking/add failed.");
        keyInput.Text = "Secret"; valueInput.Text = "changed-fixture";
        var captured = new Dictionary<string, string>(); ApplyTo(captured);
        if (captured.ContainsKey("Token") || captured["Secret"] != "changed-fixture") throw new InvalidOperationException("Pending parameter rename lost.");
        Rebind("Secret"); removeButton.PerformClick(); captured.Clear(); ApplyTo(captured);
        if (captured.Count != 1 || captured["Endpoint"] != "local://fixture") throw new InvalidOperationException("Parameter delete failed.");
        addButton.PerformClick(); keyInput.Text = "Reserved"; saveButton.PerformClick();
        if (grid.Rows.Count != 1) throw new InvalidOperationException("Reserved parameter accepted.");
        keyInput.Text = "Endpoint"; valueInput.Text = "overwrite"; saveButton.PerformClick();
        if (_values["Endpoint"] != "local://fixture") throw new InvalidOperationException("Duplicate overwrote a parameter.");
        keyInput.Text = valueInput.Text = "";
        LoadValues(captured, [], true);
        if (saveButton.Enabled || !valueInput.ReadOnly || hint.Text != DefaultHint)
            throw new InvalidOperationException("Read-only parameter editor is editable or retained an obsolete validation error.");
    }
}
