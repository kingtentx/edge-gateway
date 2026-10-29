using System.Collections;
using System.ComponentModel;

namespace EdgeGateway.WinForms;

/// <summary>Official AntdUI table with stable application rows and typed editors. No native grid or paint overrides.</summary>
public partial class GatewayTable : AntdUI.Table
{
    private object? source;
    private IBindingList? binding;
    private GatewayTableCell? current;
    private bool syncing, closingEditor;
    private GatewayTableCell? editing;
    private int updateDepth;
    private bool repaintPending;
    public GatewayTable()
    {
        Columns = new(this); Rows = new(this);
        EmptyText = "暂无数据"; MultipleRows = false; Bordered = false;
        EnableHeaderResizing = true;
        base.EditMode = AntdUI.TEditMode.None;
        base.CellClick += (_, e) =>
        {
            if (e.Record is not GatewayTableRow row || e.Column is not GatewayTableColumn col) return;
            CurrentCell = row.Cells[col.Index];
            if (!ReadOnly && CurrentCell.CanEdit && col is not GatewayTableButtonColumn && col is not GatewayTableLinkColumn && col is not GatewayGridSwitchColumn) BeginEdit(true);
        };
        base.CellButtonClick += (_, e) =>
        {
            if (e.Record is not GatewayTableRow row || e.Column is not GatewayTableColumn col) return;
            CurrentCell = row.Cells[col.Index];
            if (col is GatewayGridSelectColumn) { OpenCellEditor(); return; }
            if (CurrentCell is GatewayTableButtonCell { Enabled: false }) return;
            if (CurrentCell is GatewayTableLinkCell { Enabled: false }) return;
            CellContentClick?.Invoke(this, new(col.Index, row.Index));
        };
        base.CellDoubleClick += (_, e) =>
        {
            if (e.Record is GatewayTableRow row && e.Column is GatewayTableColumn col)
                CellDoubleClick?.Invoke(this, new(col.Index, row.Index));
        };
        base.CellFocused += (_, e) =>
        {
            if (!syncing && e.Record is GatewayTableRow row && e.Column is GatewayTableColumn col) SetCurrent(row.Cells[col.Index], false);
        };
        SelectIndexChanged += (_, _) =>
        {
            var index = SelectedIndex - 1;
            if (!syncing && index >= 0 && index < Rows.Count && Columns.Count > 0)
                SetCurrent(Rows[index].Cells[current?.ColumnIndex ?? 0], false);
        };
        ScrollBar.ValueXChanged += (_, _) => EndEdit();
        ScrollBar.ValueYChanged += (_, _) => EndEdit();
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public new GatewayTableColumns Columns { get; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GatewayTableRows Rows { get; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new object? DataSource
    {
        get => source;
        set
        {
            if (binding is not null) binding.ListChanged -= SourceChanged;
            source = value; binding = value as IBindingList;
            if (binding is not null) binding.ListChanged += SourceChanged;
            BindRows(); DataBindingComplete?.Invoke(this, new(ListChangedType.Reset));
            RepaintRows();
            DataSourceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private void SourceChanged(object? sender, ListChangedEventArgs e)
    {
        if (source is IList list && list.Count == Rows.Count && e.ListChangedType == ListChangedType.ItemChanged && e.NewIndex >= 0)
        {
            Rows[e.NewIndex].Bind(list[e.NewIndex]); RepaintRows();
        }
        else BindRows();
    }
    private void BindRows()
    {
        EndEdit(); syncing = true;
        try
        {
            ApplyColumnWidths();
            Rows.ClearCore(); current = null;
            if (source is IEnumerable list) foreach (var item in list) Rows.AddCore(item);
            base.DataSource = Rows;
            base.Refresh();
        }
        finally { syncing = false; }
        if (Rows.Count > 0 && Columns.Count > 0) CurrentCell = Rows[0].Cells[0];
        else ClearSelection();
    }
    internal void AddColumn(GatewayTableColumn column)
    {
        column.Owner = this;
        column.Render = (_, record, _) => record is GatewayTableRow row ? row.Cells[column.Index].Presentation() : null;
        base.Columns.Add(column); ApplyColumnWidths();
    }
    internal void RepaintRows()
    {
        if (syncing || IsDisposed) return;
        if (updateDepth > 0) { repaintPending = true; return; }
        ApplyColumnWidths();
        if (!ReferenceEquals(base.DataSource, Rows)) base.DataSource = Rows;
        else base.Refresh();
    }
    internal void BeginRowsUpdate() => updateDepth++;
    internal void EndRowsUpdate()
    {
        if (--updateDepth == 0 && repaintPending) { repaintPending = false; RepaintRows(); }
    }
    public GatewayTableCell? CurrentCell { get => current; set => SetCurrent(value, true); }
    private void SetCurrent(GatewayTableCell? cell, bool reveal)
    {
        if (ReferenceEquals(current, cell)) return;
        if (editing is not null && editing != cell) EndEdit();
        current = cell;
        if (cell is not null)
        {
            syncing = true;
            try
            {
                SelectedIndex = cell.RowIndex + 1;
                if (reveal && IsHandleCreated)
                {
                    ScrollLine(cell.RowIndex + 1);
                    ScrollColumn(cell.OwningColumn, select: true);
                }
            }
            finally { syncing = false; }
        }
        CurrentCellChanged?.Invoke(this, EventArgs.Empty); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    public GatewayTableRow? CurrentRow => current?.OwningRow;
    public Point CurrentCellAddress => current is null ? new(-1, -1) : new(current.ColumnIndex, current.RowIndex);
    public IEnumerable<GatewayTableRow> SelectedRows => Rows.Where(x => x.Selected);
    public new int RowCount => Rows.Count;
    public int ColumnCount => Columns.Count;
    public void ClearSelection() { SelectedIndex = -1; current = null; }
    // 2.4.16 clamps a value against (content - viewport) even when no scrollbar is shown.
    // Assigning zero in that state can produce a negative offset; leave the library's hidden scrollbar alone.
    public int HorizontalScrollingOffset { get => ScrollBar.ShowX ? ScrollBar.ValueX : 0; set { if (ScrollBar.ShowX) ScrollBar.ValueX = Math.Max(0, value); } }
    public int VerticalScrollingOffset { get => ScrollBar.ShowY ? ScrollBar.ValueY : 0; set { if (ScrollBar.ShowY) ScrollBar.ValueY = Math.Max(0, value); } }
    public int FirstDisplayedScrollingColumnIndex
    {
        get => Columns.FirstOrDefault(c => c.Visible && !c.Frozen && GetCellDisplayRectangle(c.Index, 0, false).Right > 0)?.Index ?? -1;
        set { if (value >= 0 && value < Columns.Count) ScrollColumn(Columns[value]); }
    }
    public int FirstDisplayedScrollingRowIndex
    {
        get
        {
            for (int i = 0; i < Rows.Count; i++) if (GetRowDisplayRectangle(i, false).Bottom > ColumnHeadersHeight) return i;
            return Rows.Count == 0 ? -1 : 0;
        }
        set { if (value >= 0 && value < Rows.Count) ScrollLine(value + 1, force: true); }
    }
    public Rectangle GetCellDisplayRectangle(int column, int row, bool cutOverflow)
    {
        if (column < 0 || column >= Columns.Count || !Columns[column].Visible) return Rectangle.Empty;
        var rect = CellRectangle(row + 1, Columns[column].INDEX);
        // 2.4.16 CellRectangle uses the offset of (0,0). Restore model coordinates,
        // then apply this cell's frozen-column and virtual-row offsets.
        HitTest(0, 0, out _, out _, out _, out var originX, out var originY, out _, out _);
        rect.Offset(originX - (Columns[column].Frozen ? 0 : ScrollBar.ValueX), originY - (row < 0 && FixedHeader ? 0 : ScrollBarRealY));
        if (!cutOverflow) return rect;
        var viewport = ClientRectangle;
        viewport.Y = row < 0 ? 0 : ColumnHeadersHeight;
        viewport.Height -= viewport.Y + (ScrollBar.ShowX ? ScrollBar.SIZE : 0);
        viewport.Width -= ScrollBar.ShowY ? ScrollBar.SIZE : 0;
        if (!Columns[column].Frozen)
        {
            var frozen = Columns.Where(c => c.Visible && c.Frozen).Sum(c => c.WidthPixel);
            viewport.X += frozen; viewport.Width -= frozen;
        }
        return Rectangle.Intersect(rect, viewport);
    }
    public Rectangle GetRowDisplayRectangle(int row, bool cutOverflow)
    {
        var first = Columns.FirstOrDefault(x => x.Visible);
        if (first is null) return Rectangle.Empty;
        var rect = GetCellDisplayRectangle(first.Index, row, cutOverflow); return new(0, rect.Top, ClientSize.Width, rect.Height);
    }
    public void InvalidateCell(GatewayTableCell cell) => RepaintRows();
    public void InvalidateCell(int column, int row) => RepaintRows();
    internal void ValueChanged(GatewayTableCell cell)
    {
        if (syncing) return;
        CellValueChanged?.Invoke(this, new(cell.ColumnIndex, cell.RowIndex)); RepaintRows();
    }
    public Control? EditingControl { get; private set; }
    public bool IsCurrentCellDirty { get; private set; }
    public bool IsCurrentCellInEditMode => EditingControl is not null;
    private void OpenCellEditor()
    {
        if (BeginEdit(true) && EditingControl is GatewaySelect select) select.ExpandDrop = true;
    }
    public bool BeginEdit(bool selectAll)
    {
        if (current is null || !current.CanEdit) return false;
        if (editing == current) return true;
        EndEdit(); editing = current;
        var rect = GetCellDisplayRectangle(current.ColumnIndex, current.RowIndex, true);
        Control editor;
        if (current.OwningColumn is GatewayGridSelectColumn choices)
        {
            var select = new GatewayGridSelectEditor();
            select.Bind(choices.DataSource, choices.DisplayMember, choices.ValueMember, current.Value);
            select.SelectedIndexChanged += (_, _) => { if (!closingEditor) NotifyCurrentCellDirty(true); };
            editor = select;
        }
        else
        {
            var input = new GatewayGridInputEditor { Text = current.Value?.ToString() ?? "" };
            input.TextChanged += (_, _) => NotifyCurrentCellDirty(true); editor = input;
        }
        editor.Font = Font; editor.Bounds = Rectangle.Inflate(rect, -2, -2);
        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { CloseEditor(false); Focus(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { EndEdit(); Focus(); e.Handled = true; }
        };
        editor.LostFocus += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)(() => { if (EditingControl is { ContainsFocus: false } && !closingEditor) EndEdit(); })); };
        EditingControl = editor; Controls.Add(editor); editor.BringToFront(); editor.Focus();
        EditingControlShowing?.Invoke(this, new(editor, current.Style));
        if (selectAll && editor is GatewayInput text) text.SelectAll();
        return true;
    }
    public void NotifyCurrentCellDirty(bool dirty)
    {
        IsCurrentCellDirty = dirty;
        if (!closingEditor) CurrentCellDirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }
    public bool CommitEdit(DataGridViewDataErrorContexts context)
    {
        if (editing is not null && EditingControl is { } editor)
            editing.Value = editor is GatewayGridSelectEditor select ? select.SelectedValue : editor.Text;
        IsCurrentCellDirty = false; return true;
    }
    public bool EndEdit() { CloseEditor(true); return true; }
    private void CloseEditor(bool commit)
    {
        if (closingEditor || EditingControl is null) return;
        closingEditor = true;
        try
        {
            if (commit) CommitEdit(DataGridViewDataErrorContexts.Commit);
            var editor = EditingControl; EditingControl = null; editing = null;
            Controls.Remove(editor); editor.Dispose();
        }
        finally { closingEditor = false; }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleCellKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandleCellKey(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; return; }
        base.OnKeyDown(e);
    }
    private bool HandleCellKey(Keys keyData)
    {
        if (EditingControl is null && current is { } cell)
        {
            if (keyData is Keys.Space or Keys.Enter)
            {
                if (cell is GatewayTableButtonCell { Enabled: true } or GatewayTableLinkCell { Enabled: true })
                    CellContentClick?.Invoke(this, new(cell.ColumnIndex, cell.RowIndex));
                else if (cell.OwningColumn is GatewayGridSwitchColumn && cell.CanEdit) cell.Value = cell.Value is not true;
                else OpenCellEditor();
                return true;
            }
            var row = cell.RowIndex; var col = cell.ColumnIndex;
            if (keyData == Keys.Down) row++; else if (keyData == Keys.Up) row--;
            else if (keyData == Keys.Right) { do { col++; } while (col < Columns.Count && !Columns[col].Visible); }
            else if (keyData == Keys.Left) { do { col--; } while (col >= 0 && !Columns[col].Visible); }
            else return false;
            if (row >= 0 && row < Rows.Count && col >= 0 && col < Columns.Count) CurrentCell = Rows[row].Cells[col];
            return true;
        }
        return false;
    }
    internal object? FormatCell(GatewayTableCell cell, object? value)
    {
        var args = new DataGridViewCellFormattingEventArgs(cell.ColumnIndex, cell.RowIndex, value, typeof(string), cell.Style);
        CellFormatting?.Invoke(this, args);
        return args.Value;
    }
    public void RegisterColumn(GatewayTableColumn column, int logicalMinimum)
    { column.LogicalMinimum = logicalMinimum; ApplyColumnWidths(); }
    private void ApplyColumnWidths()
    { if (Columns is not null) foreach (var col in Columns) col.ApplyWidth(); }
    internal string ColumnWidth(GatewayTableColumn column)
    {
        if (!column.FillsAvailableWidth) return column.FixedLogicalWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var visible = Columns.Where(c => c.Visible).ToArray();
        var weight = visible.Where(c => c.FillsAvailableWidth).Sum(c => Math.Max(1, c.FillWeight));
        var viewport = Math.Max(1, ClientSize.Width);
        var available = viewport - (ScrollBar.ShowY ? ScrollBar.SIZE : 0);
        var minimumWidth = visible.Sum(c => UiTheme.Scale(this, c.FillsAvailableWidth ? c.LogicalMinimum : c.FixedLogicalWidth));
        // Reserve readable minimums first, then share only the spare space.
        // Encode the minimum in Width as well: a percentage below MinWidth must
        // not depend on a second library sizing pass to produce the right total.
        // Narrow tables overflow horizontally; native drag overrides stay intact.
        var precedingWeight = visible.TakeWhile(c => c != column).Where(c => c.FillsAvailableWidth).Sum(c => Math.Max(1, c.FillWeight));
        var spare = Math.Max(0, available - minimumWidth);
        var extra = Math.Floor(spare * (precedingWeight + Math.Max(1, column.FillWeight)) / Math.Max(1d, weight))
            - Math.Floor(spare * precedingWeight / Math.Max(1d, weight));
        var target = UiTheme.Scale(this, column.LogicalMinimum) + extra;
        // AntdUI resolves percentages against the whole table then rounds up.
        // Budget the scrollbar once and avoid a fractional-pixel extra per column.
        var percent = Math.Max(0, target - .01) / viewport * 100;
        return percent.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }
    protected override void OnSizeChanged(EventArgs e)
    { ApplyColumnWidths(); base.OnSizeChanged(e); }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    { base.OnDpiChangedAfterParent(e); foreach (var col in Columns) col.ApplyWidth(); }
    protected override void Dispose(bool disposing)
    { if (disposing) { if (binding is not null) binding.ListChanged -= SourceChanged; EndEdit(); } base.Dispose(disposing); }
    public event EventHandler? CurrentCellChanged, SelectionChanged, DataSourceChanged, CurrentCellDirtyStateChanged;
    public event EventHandler<DataGridViewCellEventArgs>? CellContentClick, CellValueChanged;
    public new event EventHandler<DataGridViewCellEventArgs>? CellDoubleClick;
    public event EventHandler<DataGridViewBindingCompleteEventArgs>? DataBindingComplete;
    public event EventHandler<DataGridViewCellFormattingEventArgs>? CellFormatting;
    public event EventHandler<DataGridViewEditingControlShowingEventArgs>? EditingControlShowing;
    // Layout metadata retained for Designer compatibility; AntdUI owns all styling.
    public bool ReadOnly { get; set; } = true;
    public bool AllowUserToAddRows { get; set; }
    public bool AllowUserToDeleteRows { get; set; }
    public bool AllowUserToResizeRows { get; set; }
    public bool AutoGenerateColumns { get; set; }
    public bool RowHeadersVisible { get; set; }
    public bool EnableHeadersVisualStyles { get; set; }
    public bool MultiSelect { get => MultipleRows; set => MultipleRows = value; }
    public Color BackgroundColor { get => BackColor; set => BackColor = value; }
    public Color GridColor { get; set; }
    public BorderStyle BorderStyle { get; set; }
    public DataGridViewSelectionMode SelectionMode { get; set; }
    public DataGridViewHeaderBorderStyle ColumnHeadersBorderStyle { get; set; }
    public new DataGridViewAutoSizeColumnsMode AutoSizeColumnsMode
    {
        get => base.AutoSizeColumnsMode == AntdUI.ColumnsMode.Fill ? DataGridViewAutoSizeColumnsMode.Fill : DataGridViewAutoSizeColumnsMode.None;
        set { base.AutoSizeColumnsMode = value == DataGridViewAutoSizeColumnsMode.Fill ? AntdUI.ColumnsMode.Fill : AntdUI.ColumnsMode.Auto; ApplyColumnWidths(); }
    }
    public DataGridViewColumnHeadersHeightSizeMode ColumnHeadersHeightSizeMode { get; set; }
    public DataGridViewCellBorderStyle CellBorderStyle { get; set; }
    public new DataGridViewEditMode EditMode { get; set; }
    public DataGridViewCellStyle DefaultCellStyle { get; } = new();
    public DataGridViewCellStyle ColumnHeadersDefaultCellStyle { get; } = new();
    public int ColumnHeadersHeight => Columns.Count > 0 && CellRectangle(0, 0).Height > 0 ? CellRectangle(0, 0).Height : UiTheme.Scale(this, RowHeightHeader ?? 36);
    public GatewayTableRow RowTemplate { get; } = new();
}

public class GatewayTableColumn : AntdUI.Column
{
    internal GatewayTable? Owner;
    internal int LogicalMinimum = 90;
    private int width;
    public GatewayTableColumn() : base("", "") { Editable = false; }
    public string Name { get => Key; set => Key = value; }
    public string DataPropertyName { get; set; } = "";
    public string HeaderText { get => Title; set => Title = value; }
    public int Index => Owner?.Columns.IndexOf(this) ?? -1;
    public GatewayTable? GatewayTable => Owner;
    public int DisplayIndex { get => Index; set { } }
    public int MinimumWidth { get => Owner is null ? LogicalMinimum : UiTheme.Scale(Owner, LogicalMinimum); set { LogicalMinimum = value; ApplyWidth(); } }
    public new int Width { get => WidthPixel > 0 ? WidthPixel : width > 0 ? width : MinimumWidth; set { width = value; ApplyWidth(); } }
    internal void ApplyWidth()
    {
        MinWidth = LogicalMinimum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var value = Owner?.ColumnWidth(this) ?? FixedLogicalWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (base.Width != value) base.Width = value;
    }
    internal bool FillsAvailableWidth => AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill
        || (AutoSizeMode == DataGridViewAutoSizeColumnMode.NotSet && Owner?.AutoSizeColumnsMode == DataGridViewAutoSizeColumnsMode.Fill);
    internal int FixedLogicalWidth => Math.Max(LogicalMinimum, Owner is null || Owner.DeviceDpi == 0 ? width : width * 96 / Owner.DeviceDpi);
    public float FillWeight { get; set; } = 100;
    public bool Frozen { get => Fixed; set => Fixed = value; }
    public new bool ReadOnly { get => base.ReadOnly; set => base.ReadOnly = value; }
    public string ToolTipText { get; set; } = "";
    public DataGridViewAutoSizeColumnMode AutoSizeMode { get; set; }
    public new DataGridViewColumnSortMode SortMode { get; set; }
    public DataGridViewCellStyle DefaultCellStyle { get; } = new();
    public GatewayTableCell? CellTemplate { get; set; }
    internal virtual GatewayTableCell CreateCell() => CellTemplate is null ? new GatewayTableCell() : (GatewayTableCell)CellTemplate.Clone();
}
public class GatewayTableButtonColumn : GatewayTableColumn
{
    public GatewayTableButtonColumn() { Align = AntdUI.ColumnAlign.Center; }
    public string Text { get; set; } = "";
    public bool UseColumnTextForButtonValue { get; set; }
    public FlatStyle FlatStyle { get; set; }
    internal override GatewayTableCell CreateCell() => CellTemplate is null ? new GatewayTableButtonCell() : (GatewayTableCell)CellTemplate.Clone();
}
public class GatewayTableLinkColumn : GatewayTableColumn
{
    public GatewayTableLinkColumn() { Align = AntdUI.ColumnAlign.Center; }
    public string Text { get; set; } = "";
    public bool UseColumnTextForLinkValue { get; set; }
    public bool TrackVisitedState { get; set; }
    public LinkBehavior LinkBehavior { get; set; }
    public Color LinkColor { get; set; }
    public Color ActiveLinkColor { get; set; }
    public Color VisitedLinkColor { get; set; }
    internal override GatewayTableCell CreateCell() => new GatewayTableLinkCell();
}
public sealed class GatewayTableColumns(GatewayTable owner) : System.Collections.ObjectModel.Collection<GatewayTableColumn>
{
    public GatewayTableColumn this[string name] => this.First(x => x.Name == name);
    public bool Contains(string name) => this.Any(x => x.Name == name);
    protected override void InsertItem(int index, GatewayTableColumn item) { base.InsertItem(index, item); owner.AddColumn(item); }
    public void AddRange(params GatewayTableColumn[] items) { foreach (var item in items) Add(item); }
}
public sealed class GatewayTableRows(GatewayTable owner) : BindingList<GatewayTableRow>
{
    internal void ClearCore() { RaiseListChangedEvents = false; Clear(); RaiseListChangedEvents = true; }
    internal void AddCore(object? item)
    {
        RaiseListChangedEvents = false;
        var row = new GatewayTableRow(owner); row.Bind(item); base.Add(row); RaiseListChangedEvents = true;
    }
    public int Add(params object?[] values)
    {
        var row = new GatewayTableRow(owner);
        for (int i = 0; i < values.Length && i < row.Cells.Count; i++) row.Cells[i].SetValue(values[i]);
        base.Add(row); owner.RepaintRows(); return Count - 1;
    }
}
public class GatewayTableRow : Dictionary<string, object?>
{
    public GatewayTableRow() { Cells = new(); }
    internal GatewayTableRow(GatewayTable owner) : this()
    {
        GatewayTable = owner;
        foreach (var column in owner.Columns)
        {
            var cell = column.CreateCell(); cell.OwningColumn = column; cell.OwningRow = this; Cells.Add(cell); this[column.Name] = null;
        }
    }
    public GatewayTable? GatewayTable { get; internal set; }
    public GatewayTableCells Cells { get; }
    public object? DataBoundItem { get; private set; }
    public object? Tag { get; set; }
    public int Index => GatewayTable?.Rows.IndexOf(this) ?? -1;
    public bool ReadOnly { get; set; }
    public bool Frozen { get; set; }
    public bool Visible { get; set; } = true;
    public int Height { get => GatewayTable?.GetRowDisplayRectangle(Index, false).Height ?? 34; set { } }
    public bool Selected { get => GatewayTable?.SelectedIndex == Index + 1; set { if (value && GatewayTable is { } grid && Cells.Count > 0) grid.CurrentCell = Cells[grid.CurrentCell?.ColumnIndex ?? 0]; } }
    public DataGridViewCellStyle DefaultCellStyle { get; } = new();
    internal void Bind(object? item)
    {
        DataBoundItem = item;
        foreach (var cell in Cells)
        {
            var property = cell.OwningColumn.DataPropertyName;
            cell.SetValue(item is null || property.Length == 0 ? null : TypeDescriptor.GetProperties(item)[property]?.GetValue(item));
        }
    }
}
public sealed class GatewayTableCells : List<GatewayTableCell>
{
    public GatewayTableCell this[string name] => this.First(x => x.OwningColumn.Name == name);
}
public class GatewayTableCell
{
    public GatewayTableRow OwningRow { get; internal set; } = null!;
    public GatewayTableColumn OwningColumn { get; internal set; } = null!;
    public GatewayTable? GatewayTable => OwningRow?.GatewayTable;
    public int RowIndex => OwningRow?.Index ?? -1;
    public int ColumnIndex => OwningColumn?.Index ?? -1;
    public bool ReadOnly { get; set; }
    internal bool CanEdit => GatewayTable is { ReadOnly: false } && !ReadOnly && !OwningRow.ReadOnly && !OwningColumn.ReadOnly;
    public string ToolTipText { get; set; } = "";
    public DataGridViewCellStyle Style { get; } = new();
    public DataGridViewCellStyle InheritedStyle => Style;
    public object? Value
    {
        get => OwningRow?.GetValueOrDefault(OwningColumn.Name);
        set { if (Equals(Value, value)) return; SetValue(value); GatewayTable?.ValueChanged(this); }
    }
    internal void SetValue(object? value) { if (OwningRow is not null) OwningRow[OwningColumn.Name] = value; }
    public object? FormattedValue => OwningColumn switch
    {
        GatewayTableButtonColumn c when c.UseColumnTextForButtonValue => c.Text,
        GatewayTableLinkColumn c when c.UseColumnTextForLinkValue => c.Text,
        GatewayGridSelectColumn c => c.Display(Value),
        _ => Value
    };
    public virtual object Clone() => new GatewayTableCell();
    internal object? Presentation()
    {
        GatewayTable?.FormatCell(this, FormattedValue);
        var presentation = CreatePresentation();
        // CellButton also derives from CellLink. Suppress hover popups for all
        // row actions, including specialized buttons and select-cell triggers.
        if (presentation is AntdUI.CellLink action) action.Tooltip = null;
        return RenderedCell = presentation;
    }
    internal AntdUI.ICell? RenderedCell { get; private set; }
    internal virtual AntdUI.ICell CreatePresentation()
    {
        if (OwningColumn is GatewayGridSwitchColumn)
        {
            var toggle = new AntdUI.CellSwitch(Value is true) { Enabled = CanEdit };
            toggle.CheckedChanged += (_, e) => { if (CanEdit) Value = e.Value; };
            return toggle;
        }
        if (OwningColumn is GatewayGridSelectColumn)
            return new AntdUI.CellButton(OwningColumn.Name, FormattedValue?.ToString() ?? "请选择")
            { IconSvg = "DownOutlined", IconPosition = AntdUI.TAlignMini.Right, BorderWidth = 1, Enabled = CanEdit, Tooltip = ToolTipText };
        var fore = !Style.ForeColor.IsEmpty ? Style.ForeColor : OwningRow.DefaultCellStyle.ForeColor;
        return new AntdUI.CellText(FormattedValue?.ToString() ?? "") { Fore = fore.IsEmpty ? null : fore };
    }
    public AccessibleObject AccessibilityObject => new GatewayCellAccessibleObject(this);
    public Rectangle GetContentBounds(int rowIndex)
    {
        if (RenderedCell is not { } rendered) return Rectangle.Empty;
        var rect = rendered.Rect;
        rect.Offset(-rendered.PARENT.RECT.X, -rendered.PARENT.RECT.Y);
        return rect;
    }
}
public class GatewayTableButtonCell : GatewayTableCell
{
    public bool Enabled { get; set; } = true;
    internal override AntdUI.ICell CreatePresentation() => new AntdUI.CellButton(OwningColumn.Name, FormattedValue?.ToString() ?? "") { Enabled = Enabled, Tooltip = ToolTipText };
    public override object Clone() => new GatewayTableButtonCell { Enabled = Enabled };
}
public class GatewayTableLinkCell : GatewayTableCell
{
    public bool Enabled { get; set; } = true;
    public Color LinkColor { get; set; }
    public Color ActiveLinkColor { get; set; }
    public Color VisitedLinkColor { get; set; }
    internal override AntdUI.ICell CreatePresentation() => new AntdUI.CellLink(OwningColumn.Name, FormattedValue?.ToString() ?? "") { Enabled = Enabled, Tooltip = ToolTipText };
}
