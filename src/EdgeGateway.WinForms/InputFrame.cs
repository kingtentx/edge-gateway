namespace EdgeGateway.WinForms;

/// <summary>A button-height surface around a native, font-height editor.</summary>
internal sealed partial class InputFrame : AntdUI.Panel
{
    private Control? _editor;
    private bool _arranging;
    private Font? _measuredFont;
    private int _measuredDpi;
    private int _textHeight;

    public InputFrame()
    {
        InitializeComponent();
        TabStop = false;
        AutoSize = true;
        MinimumSize = new Size(0, UiTheme.ActionHeight);
        Height = UiTheme.ActionHeight;
    }

    public InputFrame(Control editor) : this()
    {
        Name = "InputSurface_" + editor.Name;
        TabIndex = editor.TabIndex;
        AccessibleName = editor.AccessibleName;
        Margin = editor.Margin;
        Dock = editor.Dock == DockStyle.Fill ? DockStyle.Top : editor.Dock;
        Anchor = editor.Anchor;
        MinimumSize = new Size(editor.MinimumSize.Width, UiTheme.ActionHeight);
        Size = new Size(editor.Width, UiTheme.ActionHeight);
        Controls.Add(editor);
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is null) return;
        _editor = e.Control;
        var editor = _editor;
        editor.Dock = DockStyle.None;
        editor.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        editor.Margin = Padding.Empty;
        editor.MinimumSize = Size.Empty;
        // AntdUI owns the complete field chrome, including its popup arrow and focus ring.
        if (editor is AntdUI.Input input) { BorderWidth = 0; input.BorderWidth = 1; }
        editor.Enter += OnEditorAppearanceChanged;
        editor.Leave += OnEditorAppearanceChanged;
        editor.EnabledChanged += OnEditorAppearanceChanged;
        editor.BackColorChanged += OnEditorAppearanceChanged;
        editor.SizeChanged += OnEditorSizeChanged;
        editor.BringToFront();
        UpdateSurface();
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        if (e.Control == _editor && _editor is { } editor)
        {
            editor.Enter -= OnEditorAppearanceChanged; editor.Leave -= OnEditorAppearanceChanged;
            editor.EnabledChanged -= OnEditorAppearanceChanged; editor.BackColorChanged -= OnEditorAppearanceChanged;
            editor.SizeChanged -= OnEditorSizeChanged; _editor = null;
        }
        base.OnControlRemoved(e);
    }

    private void OnEditorAppearanceChanged(object? sender, EventArgs e) => UpdateSurface();
    private void OnEditorSizeChanged(object? sender, EventArgs e) => ArrangeEditor();
    private void UpdateSurface()
    {
        if (_editor is null) return;
        BackColor = _editor is AntdUI.Input ? Color.Transparent : _editor.BackColor;
        BorderColor = _editor.ContainsFocus ? UiTheme.Blue : UiTheme.Border;
        Invalidate();
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
            if (_editor is AntdUI.Input)
            {
                var bounds = new Rectangle(Point.Empty, ClientSize);
                if (_editor.Bounds != bounds) _editor.Bounds = bounds;
                return;
            }
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
        if (_editor is null || !_editor.Enabled) return;
        _editor.Focus();
        if (_editor is ComboBox combo && e.Button == MouseButtons.Left) combo.DroppedDown = true;
    }

}
