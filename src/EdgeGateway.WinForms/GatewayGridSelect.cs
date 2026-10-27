namespace EdgeGateway.WinForms;

/// <summary>Keep typed DataGridView binding while using the official AntdUI dropdown editor.</summary>
public class GatewayGridSelectColumn : DataGridViewComboBoxColumn
{
    public GatewayGridSelectColumn() => CellTemplate = new GatewayGridSelectCell();
}

public class GatewayGridSelectCell : DataGridViewComboBoxCell
{
    public override Type EditType => typeof(GatewayGridSelectEditor);
    public override void InitializeEditingControl(int rowIndex, object initialFormattedValue, DataGridViewCellStyle style)
    {
        base.InitializeEditingControl(rowIndex, initialFormattedValue, style);
        if (DataGridView?.EditingControl is GatewayGridSelectEditor editor)
            editor.Bind(DataSource, DisplayMember, ValueMember, Value);
    }
}

public class GatewayGridSelectEditor : GatewaySelect, IDataGridViewEditingControl
{
    private bool _binding;
    private string _valueMember = "", _displayMember = "";
    public GatewayGridSelectEditor()
    {
        Format += (_, e) => e.Value = Member(e.ListItem, _displayMember)?.ToString() ?? "";
        SelectedIndexChanged += (_, _) =>
        {
            if (_binding) return;
            EditingControlValueChanged = true;
            EditingControlDataGridView?.NotifyCurrentCellDirty(true);
        };
    }
    private static object? Member(object? item, string name) => item is null || name.Length == 0 ? item
        : System.ComponentModel.TypeDescriptor.GetProperties(item)[name]?.GetValue(item);
    internal void Bind(object? source, string displayMember, string valueMember, object? value)
    {
        _binding = true;
        try
        {
            _displayMember = displayMember; _valueMember = valueMember;
            DataSource = source; SelectedValue = value; EditingControlValueChanged = false;
        }
        finally { _binding = false; }
    }
    public override object? SelectedValue
    {
        get => Member(base.SelectedValue, _valueMember);
        set => base.SelectedValue = Items.FirstOrDefault(item => Equals(Member(item, _valueMember), value));
    }
    public DataGridView? EditingControlDataGridView { get; set; }
    public object EditingControlFormattedValue
    {
        get => Text;
        set => base.SelectedValue = Items.FirstOrDefault(item => Member(item, _displayMember)?.ToString() == value?.ToString());
    }
    public int EditingControlRowIndex { get; set; }
    public bool EditingControlValueChanged { get; set; }
    public Cursor EditingPanelCursor => Cursors.Default;
    public bool RepositionEditingControlOnValueChange => false;
    public void ApplyCellStyleToEditingControl(DataGridViewCellStyle style)
    { Font = style.Font; ForeColor = style.ForeColor; BackColor = Color.White; }
    public bool EditingControlWantsInputKey(Keys keyData, bool dataGridViewWantsInputKey) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
        || !dataGridViewWantsInputKey;
    public object GetEditingControlFormattedValue(DataGridViewDataErrorContexts context) => Text;
    public void PrepareEditingControlForEdit(bool selectAll) { if (selectAll) SelectAll(); }
}
