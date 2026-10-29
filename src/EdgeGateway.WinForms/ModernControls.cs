using FontAwesome.Sharp;

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
        Type = Primary ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default;
        base.OnCreateControl();
        RefreshGlyph();
    }

    private void RefreshGlyph()
    {
        var previous = _glyphImage;
        _glyphImage = Glyph == IconChar.None ? null : Glyph.ToBitmap(IconFont.Solid,
            UiTheme.Scale(this, Navigation ? 18 : 15),
            AntdUI.Style.Get(Primary ? AntdUI.Colour.PrimaryColor : AntdUI.Colour.DefaultColor, nameof(AntdUI.Button)));
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

}

/// <summary>A page can scroll as a whole when the monitor offers very little logical height.</summary>
internal sealed class PageViewport : GatewayScrollPanel
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
            var height = Math.Max(ScrollClientSize.Height, minimumHeight);
            _page.SetBounds(AutoScrollPosition.X, AutoScrollPosition.Y, Math.Max(1, ScrollClientSize.Width), height);
        }
        finally { _arranging = false; }
    }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); PerformLayout(); }
}
