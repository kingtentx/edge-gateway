namespace EdgeGateway.WinForms;

public class GatewayGridInputColumn : DataGridViewTextBoxColumn
{
    public GatewayGridInputColumn() => CellTemplate = new GatewayGridInputCell();
}
public class GatewayGridInputCell : DataGridViewTextBoxCell
{
    public override Type EditType => typeof(GatewayGridInputEditor);
    public override void InitializeEditingControl(int rowIndex, object initialFormattedValue, DataGridViewCellStyle style)
    {
        base.InitializeEditingControl(rowIndex, initialFormattedValue, style);
        if (DataGridView?.EditingControl is GatewayGridInputEditor editor)
        { editor.Text = initialFormattedValue?.ToString() ?? ""; editor.EditingControlValueChanged = false; }
    }
}
public class GatewayGridInputEditor : GatewayInput, IDataGridViewEditingControl
{
    public GatewayGridInputEditor()
    {
        PlaceholderText = "请输入常量";
        TextChanged += (_, _) =>
        {
            EditingControlValueChanged = true;
            EditingControlDataGridView?.NotifyCurrentCellDirty(true);
        };
    }
    public DataGridView? EditingControlDataGridView { get; set; }
    public object EditingControlFormattedValue { get => Text; set => Text = value?.ToString() ?? ""; }
    public int EditingControlRowIndex { get; set; }
    public bool EditingControlValueChanged { get; set; }
    public Cursor EditingPanelCursor => Cursors.IBeam;
    public bool RepositionEditingControlOnValueChange => false;
    public void ApplyCellStyleToEditingControl(DataGridViewCellStyle style)
    { Font = style.Font; ForeColor = style.ForeColor; BackColor = Color.White; }
    public bool EditingControlWantsInputKey(Keys keyData, bool dataGridViewWantsInputKey) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End || !dataGridViewWantsInputKey;
    public object GetEditingControlFormattedValue(DataGridViewDataErrorContexts context) => Text;
    public void PrepareEditingControlForEdit(bool selectAll) { if (selectAll) SelectAll(); }
}
