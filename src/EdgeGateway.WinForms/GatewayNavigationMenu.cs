namespace EdgeGateway.WinForms;

/// <summary>Keep the icon column fixed while AntdUI hides/shows menu text.</summary>
public sealed class GatewayNavigationMenu : AntdUI.Menu
{
    private bool _aligning;

    public void SetRailCollapsed(bool collapsed)
    {
        SetCollapsed(collapsed);
        AlignIconColumn();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AlignIconColumn();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        AlignIconColumn();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        AlignIconColumn();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        AlignIconColumn();
    }

    private void AlignIconColumn()
    {
        if (_aligning || !IsHandleCreated || Items.Count == 0
            || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
        var icon = Items[0].Rect("Icon");
        if (icon.Width <= 0) return;
        var inset = UiTheme.Scale(this, 4);
        // AntdUI centers collapsed icons; expanded icons use Padding + Gap.
        // Both states meet at the center of the fixed 64 logical-pixel rail.
        var left = Collapsed ? inset : Math.Max(0,
            (UiTheme.Scale(this, 64) - icon.Width) / 2 - UiTheme.Scale(this, Gap ?? 6));
        var padding = new Padding(left, inset, inset, inset);
        if (Padding == padding) return;
        _aligning = true;
        try
        {
            Padding = padding;
            // AntdUI 2.4.16 does not recalculate item rectangles on PaddingChanged.
            // Reuse its protected size-layout path without changing actual bounds.
            base.OnSizeChanged(EventArgs.Empty);
        }
        finally { _aligning = false; }
    }
}
