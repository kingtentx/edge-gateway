using FontAwesome.Sharp;
using System.Drawing.Drawing2D;

namespace EdgeGateway.WinForms;

/// <summary>Native button semantics (keyboard/default/cancel/accessibility), with a shared visual treatment.</summary>
internal class ModernButton : GatewayButton
{
    private Bitmap? _glyphImage;
    public IconChar Glyph { get; set; }
    public bool Primary { get; set; }
    public bool Navigation { get; set; }

    public ModernButton() { Radius = 6; WaveSize = 0; }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Type = Primary ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default;
        RefreshGlyph();
    }

    private void RefreshGlyph()
    {
        var previous = _glyphImage;
        _glyphImage = Glyph == IconChar.None ? null : Glyph.ToBitmap(IconFont.Solid,
            UiTheme.Scale(this, Navigation ? 18 : 15), ForeColor);
        Icon = _glyphImage;
        previous?.Dispose();
    }

    protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); if (IsHandleCreated) RefreshGlyph(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); RefreshGlyph(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { Icon = null; _glyphImage?.Dispose(); _glyphImage = null; }
        base.Dispose(disposing);
    }

    internal static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (diameter <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }

}

internal sealed class SurfaceCard : TableLayoutPanel
{
    public Color Accent { get; set; } = UiTheme.Blue;
    public SurfaceCard() { DoubleBuffered = true; ResizeRedraw = true; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ModernButton.RoundedPath(new RectangleF(1, 1, Math.Max(1, Width - 2), Math.Max(1, Height - 2)), UiTheme.Scale(this, 12));
        using var fill = new SolidBrush(BackColor); e.Graphics.FillPath(fill, path);
        using var border = new Pen(UiTheme.Border, Math.Max(1, UiTheme.Scale(this, 1))); e.Graphics.DrawPath(border, path);
        using var accent = new SolidBrush(Accent);
        e.Graphics.FillRectangle(accent, UiTheme.Scale(this, 18), UiTheme.Scale(this, 15), UiTheme.Scale(this, 3), UiTheme.Scale(this, 18));
    }
}

/// <summary>Properties such as row templates and cell padding are not ordinary child bounds.</summary>
internal sealed class DpiDataGridView : DataGridView
{
    private readonly Dictionary<DataGridViewColumn, int> _logicalColumnWidths = [];
    private bool _updating;
    private bool _revealingColumn;
    private Font? _headerFont;
    private DpiAwareForm? _dpiForm;
    public DpiDataGridView() { DoubleBuffered = true; }
    protected override void OnColumnAdded(DataGridViewColumnEventArgs e)
    {
        base.OnColumnAdded(e);
        _logicalColumnWidths.TryAdd(e.Column, e.Column.MinimumWidth);
    }

    internal void RegisterColumn(DataGridViewColumn column, int logicalMinimum)
    {
        _logicalColumnWidths[column] = logicalMinimum;
        if (IsHandleCreated) RefreshDpiMetrics();
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var form = FindForm() as DpiAwareForm;
        if (_dpiForm != form)
        {
            if (_dpiForm is not null) _dpiForm.DpiLayoutCompleted -= OnDpiLayoutCompleted;
            _dpiForm = form;
            if (_dpiForm is not null) _dpiForm.DpiLayoutCompleted += OnDpiLayoutCompleted;
        }
        RefreshDpiMetrics();
    }
    private void OnDpiLayoutCompleted(object? sender, EventArgs e) => RefreshDpiMetrics();
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); RefreshDpiMetrics(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (IsHandleCreated) RefreshDpiMetrics(); }
    protected override void OnDataBindingComplete(DataGridViewBindingCompleteEventArgs e) { base.OnDataBindingComplete(e); RefreshDpiMetrics(); }
    protected override void OnCurrentCellChanged(EventArgs e)
    {
        base.OnCurrentCellChanged(e);
        if (_revealingColumn || _updating || !IsHandleCreated || !Visible
            || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }
            || CurrentCell?.OwningColumn is not { Visible: true, Frozen: false } column) return;
        var frozenWidth = Columns.Cast<DataGridViewColumn>().Where(item => item.Visible && item.Frozen).Sum(item => item.Width);
        var available = ClientSize.Width - frozenWidth - (RowHeadersVisible ? RowHeadersWidth : 0)
            - SystemInformation.VerticalScrollBarWidth;
        if (available <= 0 || ClientSize.Height <= ColumnHeadersHeight + SystemInformation.HorizontalScrollBarHeight) return;
        var full = GetCellDisplayRectangle(column.Index, -1, false);
        var clipped = GetCellDisplayRectangle(column.Index, -1, true);
        if (full.Width == column.Width && full.X == clipped.X && clipped.Width == full.Width) return;
        // .NET 8 ScrollColumnIntoView can abort a multi-column jump near the right
        // edge even though the target is still outside the viewport. The public
        // first-column setter includes a one-column-at-a-time correction. Run it
        // only when the current cell changes, preserving deliberate manual scrolling.
        _revealingColumn = true;
        try { FirstDisplayedScrollingColumnIndex = column.Index; }
        finally { _revealingColumn = false; }
    }
    private void RefreshDpiMetrics()
    {
        if (_updating || IsDisposed || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
        _updating = true;
        try
        {
            // Explicit DataGridViewCellStyle fonts do not follow the control's DPI font.
            // Use the current inherited grid font and rebuild only the owned bold variant.
            DefaultCellStyle.Font = Font;
            if (_headerFont is null || _headerFont.Name != Font.Name || _headerFont.Size != Font.Size || _headerFont.Unit != Font.Unit)
            {
                var previous = _headerFont;
                _headerFont = new Font(Font, FontStyle.Bold);
                ColumnHeadersDefaultCellStyle.Font = _headerFont;
                previous?.Dispose();
            }
            DefaultCellStyle.Padding = UiTheme.Scale(this, new Padding(10, 5, 10, 5));
            ColumnHeadersDefaultCellStyle.Padding = UiTheme.Scale(this, new Padding(10, 0, 10, 0));
            var textHeight = TextRenderer.MeasureText("采集 Ag", DefaultCellStyle.Font ?? Font).Height;
            var rowHeight = Math.Max(UiTheme.Scale(this, 38), textHeight + DefaultCellStyle.Padding.Vertical + UiTheme.Scale(this, 2));
            ColumnHeadersHeight = Math.Max(UiTheme.Scale(this, 40), TextRenderer.MeasureText("设备 Ag", ColumnHeadersDefaultCellStyle.Font ?? Font).Height + UiTheme.Scale(this, 12));
            RowTemplate.Height = rowHeight;
            foreach (DataGridViewRow row in Rows) if (row.Height != rowHeight) row.Height = rowHeight;
            foreach (var pair in _logicalColumnWidths)
                if (pair.Key.DataGridView == this) pair.Key.MinimumWidth = Math.Max(2, UiTheme.Scale(this, pair.Value));
        }
        finally { _updating = false; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_dpiForm is not null) _dpiForm.DpiLayoutCompleted -= OnDpiLayoutCompleted;
            ColumnHeadersDefaultCellStyle.Font = null; _headerFont?.Dispose(); _headerFont = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>A page can scroll as a whole when the monitor offers very little logical height.</summary>
internal sealed class PageViewport : Panel
{
    private readonly Control _page;
    private bool _arranging;
    public PageViewport(Control page)
    {
        AutoScroll = true; Dock = DockStyle.Fill; Margin = new Padding(0); BackColor = UiTheme.Background;
        _page = page; page.Dock = DockStyle.None; Controls.Add(page);
    }
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_arranging || _page is null) return;
        _arranging = true;
        try
        {
            var minimumHeight = UiTheme.Scale(this, 480);
            AutoScrollMinSize = new Size(0, minimumHeight);
            var height = Math.Max(ClientSize.Height, minimumHeight);
            _page.SetBounds(AutoScrollPosition.X, AutoScrollPosition.Y, Math.Max(1, ClientSize.Width), height);
        }
        finally { _arranging = false; }
    }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); PerformLayout(); }
}
