namespace EdgeGateway.WinForms;

public partial class GatewayTable
{
    protected override AccessibleObject CreateAccessibilityInstance() => new TableAccessibleObject(this);
    internal void InvokeAccessibleCell(GatewayTableCell cell)
    {
        if (IsDisposed || !Enabled || cell.RowIndex < 0) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => InvokeAccessibleCell(cell))); return; }
        CurrentCell = cell; Focus(); HandleCellKey(Keys.Space);
    }
    private sealed class TableAccessibleObject(GatewayTable table) : ControlAccessibleObject(table)
    {
        public override AccessibleRole Role => AccessibleRole.Table;
        public override int GetChildCount() => table.Rows.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < table.Rows.Count
            ? new GatewayRowAccessibleObject(table.Rows[index]) : null;
        public override AccessibleObject? GetFocused() => table.ContainsFocus ? table.CurrentCell?.AccessibilityObject : null;
        public override AccessibleObject? GetSelected() => table.CurrentRow is { } row ? new GatewayRowAccessibleObject(row) : null;
        public override AccessibleObject? HitTest(int x, int y)
        {
            var point = table.PointToClient(new Point(x, y));
            var hit = table.HitTest(point.X, point.Y, out var rowIndex, out _);
            return rowIndex > 0 && rowIndex <= table.Rows.Count && hit?.COLUMN is GatewayTableColumn column
                ? table.Rows[rowIndex - 1].Cells[column.Index].AccessibilityObject : base.HitTest(x, y);
        }
    }
}

internal sealed class GatewayRowAccessibleObject(GatewayTableRow row) : AccessibleObject
{
    public override AccessibleObject? Parent => row.GatewayTable?.AccessibilityObject;
    public override AccessibleRole Role => AccessibleRole.Row;
    public override string? Name { get => $"第 {row.Index + 1} 行"; set { } }
    public override AccessibleStates State => row.Selected ? AccessibleStates.Selected : AccessibleStates.Selectable;
    public override int GetChildCount() => row.Cells.Count(cell => cell.OwningColumn.Visible);
    public override AccessibleObject? GetChild(int index) => row.Cells.Where(cell => cell.OwningColumn.Visible).ElementAtOrDefault(index)?.AccessibilityObject;
    public override Rectangle Bounds => row.GatewayTable is { } grid
        ? grid.RectangleToScreen(grid.GetRowDisplayRectangle(row.Index, true)) : Rectangle.Empty;
}

internal sealed class GatewayCellAccessibleObject(GatewayTableCell cell) : AccessibleObject
{
    public override AccessibleObject Parent => new GatewayRowAccessibleObject(cell.OwningRow);
    public override string? Name { get => $"{cell.OwningColumn.HeaderText}：{cell.FormattedValue}"; set { } }
    public override string? Description => cell.ToolTipText;
    public override string? Value { get => cell.FormattedValue?.ToString(); set { } }
    public override AccessibleRole Role => cell switch
    {
        GatewayTableButtonCell => AccessibleRole.PushButton,
        GatewayTableLinkCell => AccessibleRole.Link,
        _ when cell.OwningColumn is GatewayGridSwitchColumn => AccessibleRole.CheckButton,
        _ when cell.OwningColumn is GatewayGridSelectColumn => AccessibleRole.ComboBox,
        _ => AccessibleRole.Cell
    };
    public override AccessibleStates State
    {
        get
        {
            var state = AccessibleStates.Focusable | AccessibleStates.Selectable;
            if (cell.GatewayTable is not { Enabled: true } || cell is GatewayTableButtonCell { Enabled: false } or GatewayTableLinkCell { Enabled: false }) state |= AccessibleStates.Unavailable;
            if (cell.GatewayTable?.CurrentCell == cell) state |= AccessibleStates.Selected;
            if (cell.GatewayTable is { ContainsFocus: true } grid && grid.CurrentCell == cell) state |= AccessibleStates.Focused;
            if (cell.OwningColumn is GatewayGridSwitchColumn && cell.Value is true) state |= AccessibleStates.Checked;
            if (Bounds.IsEmpty) state |= AccessibleStates.Offscreen;
            return state;
        }
    }
    public override Rectangle Bounds => cell.GatewayTable is { } grid
        ? grid.RectangleToScreen(grid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true)) : Rectangle.Empty;
    public override string? DefaultAction => cell is GatewayTableButtonCell { Enabled: false } or GatewayTableLinkCell { Enabled: false } ? null
        : cell is GatewayTableButtonCell or GatewayTableLinkCell ? "执行"
        : cell.CanEdit ? "编辑" : null;
    public override void DoDefaultAction() => cell.GatewayTable?.InvokeAccessibleCell(cell);
    public override int GetChildCount() => cell.GatewayTable is { IsCurrentCellInEditMode: true } grid && grid.CurrentCell == cell ? 1 : 0;
    public override AccessibleObject? GetChild(int index) => index == 0 && GetChildCount() == 1 ? cell.GatewayTable?.EditingControl?.AccessibilityObject : null;
}
