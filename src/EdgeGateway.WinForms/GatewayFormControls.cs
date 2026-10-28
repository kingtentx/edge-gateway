using System.ComponentModel;

namespace EdgeGateway.WinForms;

// These adapters preserve existing field binding contracts; rendering and popup
// interaction belong to AntdUI, not an embedded native WinForms editor.
public class GatewaySelect : AntdUI.Select
{
    private ListControlConvertEventHandler? _format;
    private object? _dataSource;
    private SelectValues? _values;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public new SelectValues Items => _values ??= new SelectValues(this);
    [Browsable(false)] public bool FormattingEnabled { get; set; }
    public event ListControlConvertEventHandler Format
    {
        add { _format += value; RebuildItems(); }
        remove { _format -= value; RebuildItems(); }
    }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? DataSource
    {
        get => _dataSource;
        set
        {
            _dataSource = value;
            Items.Replace(value is System.Collections.IEnumerable source ? source.Cast<object>().ToArray() : []);
            if (Items.Count > 0 && SelectedIndex < 0) SelectedIndex = 0;
        }
    }
    private void RebuildItems()
    {
        var selected = base.SelectedValue;
        base.Items.Clear();
        foreach (var item in Items)
        {
            var formatted = new ListControlConvertEventArgs(item.ToString(), typeof(string), item);
            _format?.Invoke(this, formatted);
            base.Items.Add(new AntdUI.SelectItem(formatted.Value?.ToString() ?? "", item));
        }
        if (selected is not null && Items.Contains(selected))
        {
            // Refresh display text as well when a formatter is attached after binding.
            var index = Items.IndexOf(selected);
            base.SelectedIndex = -1;
            base.SelectedIndex = index;
        }
        else base.SelectedIndex = -1;
    }
    public sealed class SelectValues(GatewaySelect owner) : System.Collections.ObjectModel.Collection<object>
    {
        private bool _batch;
        public void AddRange(object[] values) { _batch = true; try { foreach (var item in values) Add(item); } finally { _batch = false; owner.RebuildItems(); } }
        internal void Replace(object[] values) { _batch = true; try { Clear(); foreach (var item in values) Add(item); } finally { _batch = false; owner.RebuildItems(); } }
        protected override void InsertItem(int index, object item) { base.InsertItem(index, item); if (!_batch) owner.RebuildItems(); }
        protected override void SetItem(int index, object item) { base.SetItem(index, item); if (!_batch) owner.RebuildItems(); }
        protected override void RemoveItem(int index) { base.RemoveItem(index); if (!_batch) owner.RebuildItems(); }
        protected override void ClearItems() { base.ClearItems(); if (!_batch) owner.RebuildItems(); }
    }
    public GatewaySelect()
    {
        List = true; ListAutoWidth = false; MaxCount = 8;
        Radius = 6; Height = 38; BorderWidth = 1;
        base.ExpandDropChanged += (_, e) => { if (e.Value) DropDown?.Invoke(this, EventArgs.Empty); };
    }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new virtual object? SelectedValue { get => base.SelectedValue; set => base.SelectedValue = value; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem { get => SelectedValue; set => SelectedValue = value; }
    [DefaultValue(ComboBoxStyle.DropDownList)]
    public ComboBoxStyle DropDownStyle { get => List ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown; set => List = value == ComboBoxStyle.DropDownList; }
    [Browsable(false)] public bool IntegralHeight { get; set; }
    [Browsable(false)] public FlatStyle FlatStyle { get; set; }
    [Browsable(false)] public int DropDownWidth { get; set; }
    [Browsable(false)] public int DropDownHeight { get => MaxCount * ItemHeight; set => MaxCount = Math.Max(3, value / Math.Max(1, ItemHeight)); }
    [Browsable(false)] public int ItemHeight => TextRenderer.MeasureText("Ag采集", Font).Height + UiTheme.Scale(this, 10);
    [Browsable(false)] public bool DroppedDown { get => ExpandDrop; set => ExpandDrop = value; }
    public event EventHandler? DropDown;
}

public class GatewayNumber : AntdUI.InputNumber, ISupportInitialize
{
    public GatewayNumber()
    {
        Radius = 6; Height = 38; BorderWidth = 1;
        Minimum = 0; Maximum = 100; WheelModifyEnabled = false; ThousandsSeparator = false;
    }
    public new decimal Minimum { get => base.Minimum ?? decimal.MinValue; set => base.Minimum = value; }
    public new decimal Maximum { get => base.Maximum ?? decimal.MaxValue; set => base.Maximum = value; }
    [Browsable(false)] public BorderStyle BorderStyle { get => BorderWidth > 0 ? BorderStyle.FixedSingle : BorderStyle.None; set => BorderWidth = value == BorderStyle.None ? 0 : 1; }
    public void BeginInit() => SuspendLayout();
    public void EndInit() => ResumeLayout(false);
}

public class GatewayDatePicker : AntdUI.DatePicker
{
    public GatewayDatePicker()
    {
        Radius = 6; Height = 38; BorderWidth = 1;
        base.Value = DateTime.Today; AllowClear = false;
    }
    public new DateTime Value { get => base.Value ?? throw new InvalidOperationException("请选择有效日期。"); set => base.Value = value; }
    [Browsable(false)] public new DateTimePickerFormat Format { get; set; } = DateTimePickerFormat.Custom;
    public string CustomFormat { get => base.Format; set => base.Format = value; }
    [Browsable(false)] public Color CalendarForeColor { get => ForeColor ?? UiTheme.Ink; set => ForeColor = value; }
}

public class GatewayTabs : AntdUI.Tabs
{
    public GatewayTabs()
    {
        Type = AntdUI.TabType.Line; Gap = 16;
        Style = new AntdUI.Tabs.StyleLine { Size = 2, Padding = 8 };
        BackColor = Color.White; ForeColor = UiTheme.Ink;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
    }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AntdUI.TabCollection TabPages => Pages;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new GatewayTabPage? SelectedTab { get => base.SelectedTab as GatewayTabPage; set => base.SelectedTab = value; }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var cycle = key == Keys.Tab && keyData.HasFlag(Keys.Control);
        if (cycle || (Focused && key is Keys.Left or Keys.Right or Keys.Home or Keys.End))
        {
            var selectable = Pages.Select((page, index) => (page, index)).Where(x => x.page.Visible && x.page.Enabled).Select(x => x.index).ToArray();
            if (selectable.Length == 0) return true;
            var current = Array.IndexOf(selectable, SelectedIndex);
            var backwards = key == Keys.Left || keyData.HasFlag(Keys.Shift);
            var next = key == Keys.Home ? 0 : key == Keys.End ? selectable.Length - 1
                : (current + (backwards ? -1 : 1) + selectable.Length) % selectable.Length;
            SelectedIndex = selectable[next];
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

public class GatewayTabPage : AntdUI.TabPage
{
    public GatewayTabPage() { BackColor = Color.White; }
    public GatewayTabPage(string text) : this() { Text = text; }
}
