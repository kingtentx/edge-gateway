using System.Collections;

namespace EdgeGateway.WinForms;

/// <summary>Virtualized data rows with AntdUI checkboxes for explicit point selection.</summary>
public sealed class GatewayCheckList : GatewayScrollPanel
{
    private readonly List<AntdUI.Checkbox> _rows = [];
    private readonly List<Label> _names = [];
    private bool _arranging;
    private bool _readOnly;
    public bool ReadOnly
    {
        get => _readOnly;
        set { _readOnly = value; RenderRows(); }
    }
    private int _selectedIndex = -1;
    public GatewayCheckList()
    {
        AutoScroll = true; BackColor = Color.White; TabStop = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer, true);
        Items = new PointItems(this);
    }
    public PointItems Items { get; }
    public bool CheckOnClick { get; set; } = true;
    public bool IntegralHeight { get; set; }
    public new BorderStyle BorderStyle { get; set; }
    private int RowHeight => Math.Max(UiTheme.Scale(this, 36), Font.Height + UiTheme.Scale(this, 12));
    public int SelectedIndex
    {
        get => _selectedIndex;
        set { _selectedIndex = value; RenderRows(); }
    }
    public int TopIndex
    {
        get => -AutoScrollPosition.Y / RowHeight;
        set { AutoScrollPosition = new Point(0, Math.Max(0, value) * RowHeight); RenderRows(); }
    }
    public Rectangle GetItemRectangle(int index) => new(0, index * RowHeight + AutoScrollPosition.Y, ScrollClientSize.Width, RowHeight);
    public event ItemCheckEventHandler? ItemCheck;
    public void SetItemChecked(int index, bool value)
    {
        if (ReadOnly) { RenderRows(); return; }
        var args = new ItemCheckEventArgs(index, value ? CheckState.Checked : CheckState.Unchecked,
            Items.Checked[index] ? CheckState.Checked : CheckState.Unchecked);
        ItemCheck?.Invoke(this, args);
        Items.Checked[index] = args.NewValue == CheckState.Checked;
        RenderRows();
    }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); RenderRows(); }
    protected override void OnViewportScroll() { base.OnViewportScroll(); RenderRows(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RenderRows(); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Items.Count > 0 && e.KeyCode is Keys.Up or Keys.Down)
        {
            SelectedIndex = Math.Clamp(SelectedIndex + (e.KeyCode == Keys.Up ? -1 : 1), 0, Items.Count - 1);
            if (!ClientRectangle.Contains(GetItemRectangle(SelectedIndex))) TopIndex = SelectedIndex;
            Focus();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Space && SelectedIndex >= 0)
        { SetItemChecked(SelectedIndex, !Items.Checked[SelectedIndex]); e.Handled = true; }
        base.OnKeyDown(e);
    }
    private void RenderRows()
    {
        if (_arranging || !IsHandleCreated || Items is null) return;
        _arranging = true;
        try
        {
            var extent = new Size(0, Items.Count * RowHeight);
            if (AutoScrollMinSize != extent) AutoScrollMinSize = extent;
            var first = TopIndex;
            var count = Math.Min(Items.Count - first, Math.Max(0, ScrollClientSize.Height / RowHeight + 2));
            while (_rows.Count < count)
            {
                var row = new AntdUI.Checkbox { AutoSizeMode = AntdUI.TAutoSize.None, TabStop = false };
                var name = new Label { AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
                name.Click += (_, _) =>
                {
                    if (row.Tag is not int index || !Enabled) return;
                    _selectedIndex = index;
                    if (CheckOnClick) SetItemChecked(index, !Items.Checked[index]);
                    else RenderRows();
                    row.Focus();
                };
                row.KeyDown += (_, e) => { if (e.KeyCode is Keys.Up or Keys.Down) OnKeyDown(e); };
                row.CheckedChanged += (_, _) =>
                {
                    if (!_arranging && row.Tag is int index)
                    { _selectedIndex = index; SetItemChecked(index, row.Checked); }
                };
                _rows.Add(row); Controls.Add(row);
                _names.Add(name); Controls.Add(name);
            }
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i]; row.Visible = i < count;
                var name = _names[i]; name.Visible = i < count;
                if (i >= count) continue;
                var index = first + i;
                row.Tag = index; row.Text = "";
                row.AccessibleName = Items[index].ToString() ?? "";
                var bounds = GetItemRectangle(index);
                var checkWidth = UiTheme.Scale(this, 34);
                row.Checked = Items.Checked[index];
                row.Enabled = !ReadOnly;
                row.Bounds = new Rectangle(bounds.Left, bounds.Top, checkWidth, bounds.Height);
                row.BackColor = index == SelectedIndex ? UiTheme.BlueSoft : Color.White;
                name.Text = row.AccessibleName;
                name.AccessibleName = row.AccessibleName;
                name.Bounds = new Rectangle(bounds.Left + checkWidth, bounds.Top, Math.Max(0, bounds.Width - checkWidth), bounds.Height);
                name.BackColor = row.BackColor;
            }
        }
        finally { _arranging = false; }
    }
    public sealed class PointItems(GatewayCheckList owner) : IEnumerable<object>
    {
        private readonly List<object> _values = [];
        internal readonly List<bool> Checked = [];
        public int Count => _values.Count;
        public object this[int index] => _values[index];
        public void Clear() { _values.Clear(); Checked.Clear(); owner._selectedIndex = -1; owner.AutoScrollPosition = Point.Empty; owner.RenderRows(); }
        internal void Replace(IEnumerable<(object Item, bool IsChecked)> items)
        {
            owner._arranging = true;
            try
            {
                _values.Clear(); Checked.Clear(); owner._selectedIndex = -1;
                owner.AutoScrollPosition = Point.Empty;
                foreach (var (item, isChecked) in items) { _values.Add(item); Checked.Add(isChecked); }
            }
            finally { owner._arranging = false; }
            owner.RenderRows();
        }
        public int Add(object item, bool isChecked)
        { _values.Add(item); Checked.Add(isChecked); owner.RenderRows(); return Count - 1; }
        public IEnumerator<object> GetEnumerator() => _values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
