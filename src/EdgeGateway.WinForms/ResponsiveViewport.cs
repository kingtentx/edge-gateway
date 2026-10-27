namespace EdgeGateway.WinForms;

    internal sealed class ResponsiveViewport : Panel, IMessageFilter
{
        private Control? _content;
        private Func<int> _minimumHeight = () => 480;
        private bool _arranging;
        [System.ComponentModel.DefaultValue(false)]
        public bool RouteContentWheel { get; set; }

        public ResponsiveViewport()
        { Dock = DockStyle.Fill; AutoScroll = true; Margin = Padding.Empty; }

        internal void BindLayout(Control content, Func<int> minimumHeight)
        { _content = content; _minimumHeight = minimumHeight; PerformLayout(); }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            if (_arranging) return;
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
                    var bounds = new Rectangle(Padding.Left + AutoScrollPosition.X, Padding.Top + AutoScrollPosition.Y, width, height);
                    if (_content.Bounds != bounds) _content.Bounds = bounds;
                    if (width == measuredWidth && width == Math.Max(1, ClientSize.Width - Padding.Horizontal)) break;
                }
            }
            finally { _arranging = false; }
        }

        protected override void OnHandleCreated(EventArgs e)
        { base.OnHandleCreated(e); System.Windows.Forms.Application.AddMessageFilter(this); }
        protected override void OnHandleDestroyed(EventArgs e)
        { System.Windows.Forms.Application.RemoveMessageFilter(this); base.OnHandleDestroyed(e); }

        public bool PreFilterMessage(ref Message m)
        {
            if (!RouteContentWheel || m.Msg != 0x020A || !Visible || !VerticalScroll.Visible || !IsHandleCreated) return false;
            var location = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            if (!RectangleToScreen(ClientRectangle).Contains(location)) return false;
            var target = Control.FromChildHandle(m.HWnd);
            if (target?.FindForm() != FindForm()) return false;
            var delta = unchecked((short)((long)m.WParam >> 16));
            var lines = SystemInformation.MouseWheelScrollLines;
            var step = lines < 0 ? ClientSize.Height : Math.Max(1, lines) * Font.Height;
            AutoScrollPosition = new Point(0, Math.Max(0, -AutoScrollPosition.Y - delta * step / 120));
            return true;
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            PerformLayout();
        }
    }
