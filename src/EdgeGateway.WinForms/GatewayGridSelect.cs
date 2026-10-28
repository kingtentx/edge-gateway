namespace EdgeGateway.WinForms;

public class GatewayGridSelectColumn : GatewayTableColumn
{
    public object? DataSource { get; set; }
    public string DisplayMember { get; set; } = "";
    public string ValueMember { get; set; } = "";
    internal string Display(object? value)
    {
        if (DataSource is System.Collections.IEnumerable items)
            foreach (var item in items)
                if (Equals(Member(item, ValueMember), value)) return Member(item, DisplayMember)?.ToString() ?? "";
        return value?.ToString() ?? "";
    }
    internal static object? Member(object? item, string name) => item is null || name.Length == 0 ? item
        : System.ComponentModel.TypeDescriptor.GetProperties(item)[name]?.GetValue(item);
}

public class GatewayGridSelectEditor : GatewaySelect
{
    private string valueMember = "", displayMember = "";
    public GatewayGridSelectEditor() => Format += (_, e) => e.Value = GatewayGridSelectColumn.Member(e.ListItem, displayMember)?.ToString() ?? "";
    internal void Bind(object? source, string display, string value, object? selected)
    {
        displayMember = display; valueMember = value; DataSource = source; SelectedValue = selected;
    }
    public override object? SelectedValue
    {
        get => GatewayGridSelectColumn.Member(base.SelectedValue, valueMember);
        set => base.SelectedValue = Items.FirstOrDefault(item => Equals(GatewayGridSelectColumn.Member(item, valueMember), value));
    }
}
