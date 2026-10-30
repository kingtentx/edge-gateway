using EdgeGateway.Model;
using System.Globalization;

namespace EdgeGateway.WinForms;

internal sealed partial class DriverCustomParametersView : UserControl
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private Dictionary<string, DriverParameterDescriptor> _metadata = new(StringComparer.Ordinal);
    private Dictionary<string, DeviceParameterMetadata> _display = new(StringComparer.Ordinal);
    private bool _readOnly;
    public DriverCustomParametersView()
    {
        InitializeComponent();
        addButton.Click += (_, _) => EditParameter(null);
        editButton.Click += (_, _) => EditParameter(SelectedKey);
        removeButton.Click += (_, _) => { if (SelectedKey is { } key && !_readOnly && !_metadata.ContainsKey(key)) { _values.Remove(key); _display.Remove(key); Rebind(); } };
        grid.CellDoubleClick += (_, _) => EditParameter(SelectedKey);
        grid.SelectionChanged += (_, _) => UpdateCommands();
        search.TextChanged += (_, _) => Rebind(SelectedKey);
    }
    private string? SelectedKey => (grid.CurrentRow?.DataBoundItem as ParameterRow)?.Key;
    internal static bool IsSecret(string key) => new[] { "password", "token", "secret", "authorization", "密码", "授权" }
        .Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));
    private bool Locked(string key) => _metadata.TryGetValue(key, out var meta) && (meta.IsReadOnly || meta.BoundProperty is not null);
    private void UpdateCommands()
    {
        addButton.Enabled = !_readOnly;
        editButton.Enabled = !_readOnly && SelectedKey is { } key && !Locked(key);
        removeButton.Enabled = !_readOnly && SelectedKey is { } selected && !_metadata.ContainsKey(selected);
    }
    private void EditParameter(string? key)
    {
        if (_readOnly || key is not null && Locked(key)) return;
        _metadata.TryGetValue(key ?? "", out var metadata);
        using var editor = new DeviceParameterDialog(key ?? "", key is null ? "" : _values[key], metadata is not null,
            metadata?.IsSecret == true, candidate => candidate != key && _values.ContainsKey(candidate), DisplayMetadata(key ?? "", metadata));
        if (editor.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (key is not null && key != editor.ParameterKey) { _values.Remove(key); _display.Remove(key); }
        _values[editor.ParameterKey] = editor.ParameterValue;
        _display[editor.ParameterKey] = editor.Metadata;
        Rebind(editor.ParameterKey);
    }
    public void LoadDriver(DriverDescriptor descriptor, DeviceConfiguration device, bool readOnly)
    {
        _metadata = descriptor.Parameters.ToDictionary(p => p.Key, StringComparer.Ordinal);
        _display = device.ParameterMetadata.ToDictionary(pair => pair.Key, pair => new DeviceParameterMetadata { Name = pair.Value.Name, TypeName = pair.Value.TypeName, Description = pair.Value.Description }, StringComparer.Ordinal);
        _values.Clear();
        foreach (var pair in device.Properties) _values[pair.Key] = pair.Value;
        foreach (var meta in descriptor.Parameters)
            _values[meta.Key] = BoundValue(meta, device) ?? (meta.DeviceProperty is { } property
                ? Convert.ToString(ConnectionProperty(property).GetValue(device), CultureInfo.InvariantCulture) ?? ""
                : device.Properties.GetValueOrDefault(meta.Key, meta.DefaultValue));
        _readOnly = readOnly; search.Text = ""; Rebind();
    }
    public void RefreshBindings(DeviceConfiguration device)
    {
        foreach (var meta in _metadata.Values)
            if (BoundValue(meta, device) is { } value) _values[meta.Key] = value;
        Rebind(SelectedKey);
    }
    public void ApplyToDevice(DeviceConfiguration device)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in _values)
        {
            if (_metadata.TryGetValue(pair.Key, out var meta))
            {
                if (meta.BoundProperty is not null) continue;
                if (meta.DeviceProperty is { } name)
                {
                    var property = ConnectionProperty(name);
                    try { property.SetValue(device, Convert.ChangeType(pair.Value, property.PropertyType, CultureInfo.InvariantCulture)); }
                    catch (Exception ex) when (ex is FormatException or OverflowException)
                    { throw new InvalidOperationException($"参数 {meta.Name}（{meta.Key}）格式错误：应为 {meta.TypeName}。", ex); }
                    continue;
                }
            }
            properties.Add(pair.Key, pair.Value);
        }
        device.Properties = properties;
        device.ParameterMetadata = _display.Where(pair => _values.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key,
            pair => new DeviceParameterMetadata { Name = pair.Value.Name, TypeName = pair.Value.TypeName, Description = pair.Value.Description }, StringComparer.Ordinal);
    }
    private static System.Reflection.PropertyInfo ConnectionProperty(string name)
    {
        if (name is not ("Host" or "Port" or "UnitId" or "SerialPortName" or "BaudRate" or "DataBits" or "Parity" or "StopBits"))
            throw new InvalidOperationException("不支持的驱动连接字段：" + name);
        return typeof(DeviceConfiguration).GetProperty(name)!;
    }
    private static string? BoundValue(DriverParameterDescriptor meta, DeviceConfiguration device) => meta.BoundProperty switch
    {
        "Id" => device.Id.ToString(), "TimeoutMs" => device.TimeoutMs.ToString(CultureInfo.InvariantCulture),
        "PollIntervalMs" => device.PollIntervalMs.ToString(CultureInfo.InvariantCulture), _ => null
    };
    private DeviceParameterMetadata DisplayMetadata(string key, DriverParameterDescriptor? meta)
    {
        _display.TryGetValue(key, out var saved);
        return new DeviceParameterMetadata { Name = saved?.Name ?? meta?.Name ?? key,
            TypeName = meta?.TypeName ?? saved?.TypeName ?? "String", Description = saved?.Description ?? meta?.Description ?? "" };
    }
    private void Rebind(string? key = null)
    {
        var query = search.Text.Trim();
        var rows = _values.Select(pair =>
        {
            _metadata.TryGetValue(pair.Key, out var meta);
            var display = DisplayMetadata(pair.Key, meta);
            var description = display.Description;
            if (meta?.BoundProperty is not null) description = "由通用设置自动关联（只读）";
            else if (meta?.IsReadOnly == true) description = "只读 · " + description;
            else if (meta?.Options.Count > 0) description += " 可选：" + string.Join("、", meta.Options);
            return new ParameterRow(pair.Key, display.Name,
                (meta?.IsSecret == true || IsSecret(pair.Key)) && pair.Value.Length > 0 ? "••••••" : pair.Value,
                display.TypeName, description);
        }).Where(row => query.Length == 0 || row.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        GridViewState.Rebind(grid, rows, row => row.Key, key);
        UpdateCommands();
    }
    internal void OpenForSmoke(string? key) => EditParameter(key);
    internal IReadOnlyDictionary<string, string> Values => _values;
    internal void SetForSmoke(string key, string value)
    {
        if (_readOnly || Locked(key)) throw new InvalidOperationException("参数只读。");
        _values[key] = value; Rebind(key);
    }
    internal void VerifyEditingForSmoke()
    {
        SetForSmoke("Endpoint", "fixture://设备"); SetForSmoke("Token", "fake-token");
        if (grid.Rows.Single(row => ((ParameterRow)row.DataBoundItem!).Key == "Token").Cells["Value"].Value?.ToString() != "••••••")
            throw new InvalidOperationException("Secret parameter was exposed.");
        Rebind("Endpoint"); removeButton.PerformClick();
        if (_values.ContainsKey("Endpoint")) throw new InvalidOperationException("Manual parameter delete failed.");
    }
    private sealed record ParameterRow(string Key, string Name, string Value, string Type, string Description);
}
