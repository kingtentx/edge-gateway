namespace EdgeGateway.WinForms;

/// <summary>Official AntdUI scrolling with access to the native layout viewport.
/// This adapter does not draw or replace the component's scrollbars.</summary>
public class GatewayScrollPanel : AntdUI.Panel
{
    private Size _scrollExtent;
    private Point _pendingPosition;
    internal ScrollableControl? ScrollHost => Controls.Owner as ScrollableControl;
    internal Size ScrollClientSize => ScrollHost?.ClientSize ?? ClientSize;
    public GatewayScrollPanel() { AutoScroll = true; Radius = 0; }

    public Size AutoScrollMinSize
    {
        get => ScrollHost?.AutoScrollMinSize ?? _scrollExtent;
        set { _scrollExtent = value; if (ScrollHost is { } host) host.AutoScrollMinSize = value; }
    }
    public Point AutoScrollPosition
    {
        get => ScrollHost?.AutoScrollPosition ?? Point.Empty;
        set
        {
            _pendingPosition = value;
            if (ScrollHost is { } host)
            {
                host.AutoScrollPosition = value;
                OnViewportScroll();
            }
        }
    }
    public VScrollProperties VerticalScroll => ScrollHost!.VerticalScroll;
    public HScrollProperties HorizontalScroll => ScrollHost!.HorizontalScroll;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (ScrollHost is not { } host) return;
        host.AutoScrollMinSize = _scrollExtent;
        host.AutoScrollPosition = _pendingPosition;
        host.Scroll -= HostScrolled;
        host.Scroll += HostScrolled;
        host.SizeChanged -= HostResized;
        host.SizeChanged += HostResized;
        if (YScroll is { } bar)
        {
            bar.ValueChanged -= BarScrolled;
            bar.ValueChanged += BarScrolled;
        }
    }
    private void BarScrolled(object? sender, AntdUI.IntEventArgs e) => OnViewportScroll();
    private void HostScrolled(object? sender, ScrollEventArgs e) => OnViewportScroll();
    private void HostResized(object? sender, EventArgs e) => PerformLayout();
    protected virtual void OnViewportScroll() { }
}
