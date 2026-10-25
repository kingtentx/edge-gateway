namespace EdgeGateway.WinForms;

/// <summary>A button-height surface around a native, font-height editor.</summary>
internal sealed class InputFrame : Panel
{
    private readonly Control _editor;
    private bool _arranging;
    private Font? _measuredFont;
    private int _measuredDpi;
    private int _textHeight;

    public InputFrame(Control editor)
    {
        _editor = editor;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Name = "InputSurface_" + editor.Name;
        TabStop = false;
        TabIndex = editor.TabIndex;
        AccessibleName = editor.AccessibleName;
        Margin = editor.Margin;
        Dock = editor.Dock == DockStyle.Fill ? DockStyle.Top : editor.Dock;
        Anchor = editor.Anchor;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(editor.MinimumSize.Width, UiTheme.ActionHeight);
        Size = new Size(editor.Width, UiTheme.ActionHeight);
        editor.Dock = DockStyle.None;
        editor.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        editor.Margin = Padding.Empty;
        editor.MinimumSize = Size.Empty;
        if (editor is TextBoxBase text) text.BorderStyle = BorderStyle.None;
        if (editor is NumericUpDown number) number.BorderStyle = BorderStyle.None;
        Controls.Add(editor);
        editor.Enter += (_, _) => Invalidate();
        editor.Leave += (_, _) => Invalidate();
        editor.EnabledChanged += (_, _) => Invalidate();
        editor.BackColorChanged += (_, _) => { BackColor = editor.BackColor; Invalidate(); };
        editor.SizeChanged += (_, _) => ArrangeEditor();
        BackColor = editor.BackColor;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (!ReferenceEquals(_measuredFont, Font) || _measuredDpi != DeviceDpi)
        {
            _measuredFont = Font;
            _measuredDpi = DeviceDpi;
            _textHeight = TextRenderer.MeasureText("确定", Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;
        }
        return new Size(Width, Math.Max(UiTheme.Scale(this, UiTheme.ActionHeight), _textHeight + UiTheme.Scale(this, 14)));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        ArrangeEditor();
    }

    private void ArrangeEditor()
    {
        if (_editor is null || _editor.Parent != this || _arranging
            || FindForm() is not DpiAwareForm { IsDpiLayoutReady: true }) return;
        _arranging = true;
        try
        {
            var inset = UiTheme.Scale(this, 8);
            // Native editors own their font-dependent height and handle lifecycle;
            // the surface only sizes the available width and centers the editor.
            var top = Math.Max(0, (ClientSize.Height - _editor.Height) / 2);
            var width = Math.Max(1, ClientSize.Width - inset * 2);
            // Even an unchanged SetBounds can ask an AutoSize ancestor to lay out
            // again. Avoid a whole table measurement on every descendant layout pass.
            if (_editor.Left != inset || _editor.Top != top || _editor.Width != width)
                _editor.SetBounds(inset, top, width, 0, BoundsSpecified.X | BoundsSpecified.Y | BoundsSpecified.Width);
        }
        finally { _arranging = false; }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_editor.Enabled) return;
        _editor.Focus();
        if (_editor is ComboBox combo && e.Button == MouseButtons.Left) combo.DroppedDown = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Background);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = ModernButton.RoundedPath(new RectangleF(1, 1, Math.Max(1, Width - 2), Math.Max(1, Height - 2)), UiTheme.Scale(this, 8));
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, path);
        using var pen = new Pen(_editor.ContainsFocus ? UiTheme.Blue : UiTheme.Border, Math.Max(1, UiTheme.Scale(this, 1)));
        e.Graphics.DrawPath(pen, path);
    }
}
