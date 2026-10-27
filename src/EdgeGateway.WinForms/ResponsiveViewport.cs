namespace EdgeGateway.WinForms;

    internal sealed class ResponsiveViewport : Panel
{
        private Control? _content;
        private Func<int> _minimumHeight = () => 480;
        private bool _arranging;

        public ResponsiveViewport()
        { Dock = DockStyle.Fill; AutoScroll = true; Margin = Padding.Empty; }

        internal void BindLayout(Control content, Func<int> minimumHeight)
        { _content = content; _minimumHeight = minimumHeight; PerformLayout(); }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_arranging || _content is null || FindForm() is not DpiAwareForm { IsDpiLayoutReady: true }) return;
            _arranging = true;
            try
            {
                // A vertical scrollbar can narrow the content enough to wrap a
                // toolbar onto another row. Re-measure at that actual width so
                // wrapped actions cannot consume the tables' minimum height.
                for (var pass = 0; pass < 4; pass++)
                {
                    var measuredWidth = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                    if (_content.Width != measuredWidth) _content.Width = measuredWidth;
                    _content.PerformLayout();
                    var minimum = Math.Max(1, _minimumHeight());
                    var scrollExtent = new Size(0, minimum + Padding.Vertical);
                    if (AutoScrollMinSize != scrollExtent) AutoScrollMinSize = scrollExtent;
                    var width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                    var height = Math.Max(minimum, ClientSize.Height - Padding.Vertical);
                    _content.SetBounds(Padding.Left + AutoScrollPosition.X, Padding.Top + AutoScrollPosition.Y, width, height);
                    if (width == measuredWidth && width == Math.Max(1, ClientSize.Width - Padding.Horizontal)) break;
                }
            }
            finally { _arranging = false; }
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            PerformLayout();
        }
    }
