namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private static void SetWrappingWidth(Label label, int availableWidth)
    {
        var maximum = new Size(Math.Max(1, availableWidth - label.Margin.Horizontal), 0);
        if (label.MaximumSize != maximum) label.MaximumSize = maximum;
    }

    private Panel ScrollableContent(Control content, string name, Func<int> minimumHeight)
    {
        var viewport = new ContentScrollViewport(content, minimumHeight) { Name = name };
        DpiLayoutCompleted += (_, _) => viewport.PerformLayout();
        return viewport;
    }

    /// <summary>
    /// Keeps a useful viewport for tables and charts when the window is short. Only
    /// layout dimensions are recalculated; framework DPI scaling owns the controls.
    /// </summary>
    private sealed class ContentScrollViewport : Panel
    {
        private readonly Control _content;
        private readonly Func<int> _minimumHeight;
        private bool _arranging;

        public ContentScrollViewport(Control content, Func<int> minimumHeight)
        {
            Dock = DockStyle.Fill; AutoScroll = true; Margin = new Padding(0);
            _content = content; _minimumHeight = minimumHeight;
            content.Dock = DockStyle.None; content.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            content.Margin = new Padding(0); Controls.Add(content);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_arranging || _content is null || FindForm() is not DpiAwareForm { IsDpiLayoutReady: true }) return;
            _arranging = true;
            try
            {
                var width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                if (_content.Width != width) _content.Width = width;
                var minimum = Math.Max(1, _minimumHeight());
                var height = Math.Max(minimum, ClientSize.Height - Padding.Vertical);
                var scrollExtent = new Size(0, minimum + Padding.Vertical);
                if (AutoScrollMinSize != scrollExtent) AutoScrollMinSize = scrollExtent;
                // Adding/removing the vertical scrollbar changes ClientSize. Re-read
                // it so a newly visible scrollbar never causes horizontal overflow.
                width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                _content.SetBounds(Padding.Left + AutoScrollPosition.X, Padding.Top + AutoScrollPosition.Y, width, height);
            }
            finally { _arranging = false; }
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            PerformLayout();
        }
    }
}
