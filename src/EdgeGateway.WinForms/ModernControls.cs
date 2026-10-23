using FontAwesome.Sharp;
using System.Drawing.Drawing2D;

namespace EdgeGateway.WinForms;

/// <summary>Native button semantics (keyboard/default/cancel/accessibility), with a shared visual treatment.</summary>
internal sealed class ModernButton : Button
{
    private Bitmap? _glyphImage;
    private int _glyphSize;
    private Color _glyphColor;
    private bool _hover, _pressed;
    public IconChar Glyph { get; init; }
    public bool Primary { get; init; }
    public bool Navigation { get; init; }

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var icon = Glyph == IconChar.None ? 0 : UiTheme.Scale(this, Navigation ? 29 : 25);
        return new Size(Math.Max(MinimumSize.Width, text.Width + icon + Padding.Horizontal + UiTheme.Scale(this, 4)),
            Math.Max(MinimumSize.Height, text.Height + Padding.Vertical + UiTheme.Scale(this, 2)));
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor ?? UiTheme.Background);

    protected override void OnPaint(PaintEventArgs e)
    {
        // ButtonBase is Opaque and normal WM_PAINT skips OnPaintBackground.
        // Fill the complete surface so the pixels outside rounded corners are stable.
        OnPaintBackground(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var background = !Enabled ? Color.FromArgb(239, 242, 247) : _pressed
            ? ControlPaint.Dark(BackColor, .08f) : _hover ? (Navigation ? Color.FromArgb(35, 55, 83) : ControlPaint.Light(BackColor, .15f)) : BackColor;
        var foreground = Enabled ? ForeColor : Color.FromArgb(153, 166, 184);
        using var path = RoundedPath(new RectangleF(1, 1, Math.Max(1, Width - 2), Math.Max(1, Height - 2)), UiTheme.Scale(this, 8));
        using var brush = new SolidBrush(background);
        graphics.FillPath(brush, path);
        if (!Navigation && !Primary)
        {
            using var border = new Pen(_hover && Enabled ? UiTheme.Blue : UiTheme.Border, Math.Max(1, UiTheme.Scale(this, 1)));
            graphics.DrawPath(border, path);
        }
        if (Navigation && BackColor != UiTheme.Navy)
        {
            using var indicator = new SolidBrush(Color.FromArgb(87, 179, 255));
            graphics.FillRectangle(indicator, UiTheme.Scale(this, 2), Height / 4, UiTheme.Scale(this, 3), Height / 2);
        }
        var iconSize = Glyph == IconChar.None ? 0 : UiTheme.Scale(this, Navigation ? 18 : 15);
        var gap = iconSize == 0 ? 0 : UiTheme.Scale(this, 9);
        var measured = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var left = Navigation ? Padding.Left : Math.Max(Padding.Left, (Width - iconSize - gap - measured.Width) / 2);
        if (iconSize > 0)
        {
            if (_glyphImage is null || _glyphSize != iconSize || _glyphColor != foreground)
            {
                _glyphImage?.Dispose();
                _glyphImage = Glyph.ToBitmap(IconFont.Solid, iconSize, foreground);
                _glyphSize = iconSize; _glyphColor = foreground;
            }
            graphics.DrawImageUnscaled(_glyphImage, left, (Height - iconSize) / 2);
            left += iconSize + gap;
        }
        TextRenderer.DrawText(graphics, Text, Font, new Rectangle(left, 0, Math.Max(1, Width - left - Padding.Right), Height), foreground,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -UiTheme.Scale(this, 5), -UiTheme.Scale(this, 5)), foreground, background);
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

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e); _glyphImage?.Dispose(); _glyphImage = null; Invalidate();
    }
    protected override void Dispose(bool disposing) { if (disposing) _glyphImage?.Dispose(); base.Dispose(disposing); }
}

internal sealed class SurfaceCard : TableLayoutPanel
{
    public Color Accent { get; init; } = UiTheme.Blue;
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
    private Font? _headerFont;
    private DpiAwareForm? _dpiForm;
    public DpiDataGridView() { DoubleBuffered = true; }
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
            DefaultCellStyle.Padding = UiTheme.Scale(this, new Padding(10, 6, 10, 6));
            ColumnHeadersDefaultCellStyle.Padding = UiTheme.Scale(this, new Padding(10, 0, 10, 0));
            var textHeight = TextRenderer.MeasureText("采集 Ag", DefaultCellStyle.Font ?? Font).Height;
            var rowHeight = Math.Max(UiTheme.Scale(this, 40), textHeight + DefaultCellStyle.Padding.Vertical + UiTheme.Scale(this, 2));
            ColumnHeadersHeight = Math.Max(UiTheme.Scale(this, 44), TextRenderer.MeasureText("设备 Ag", ColumnHeadersDefaultCellStyle.Font ?? Font).Height + UiTheme.Scale(this, 14));
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
