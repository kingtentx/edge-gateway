namespace EdgeGateway.WinForms;

/// <summary>Layout rules shared by the editor forms and newly created driver fields.</summary>
internal static class EditorLayout
{
    public static TableLayoutPanel Fields(int labelWidth = 168) => new FieldTable(labelWidth);

    public static void Section(TableLayoutPanel table, string title, string? description = null)
    {
        var section = new TableLayoutPanel
        {
            Name = "EditorSection_" + table.RowCount, Dock = DockStyle.Fill,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = string.IsNullOrWhiteSpace(description) ? 1 : 2,
            BackColor = UiTheme.Background, Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, table.RowCount == 0 ? 0 : 14, 0, 10)
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = WrappingLabel(title, 11, UiTheme.Ink, true);
        heading.Margin = Padding.Empty;
        section.Controls.Add(heading, 0, 0);
        if (!string.IsNullOrWhiteSpace(description))
        {
            section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var detail = WrappingLabel(description, 10, UiTheme.Muted);
            detail.Margin = new Padding(0, 3, 0, 0);
            section.Controls.Add(detail, 0, 1);
        }
        section.SizeChanged += (_, _) =>
        {
            foreach (var label in section.Controls.OfType<Label>())
                label.MaximumSize = new Size(Math.Max(1, section.ClientSize.Width - section.Padding.Horizontal - label.Margin.Horizontal), 0);
        };
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(section, 0, row);
        table.SetColumnSpan(section, 2);
    }

    public static void FieldNote(TableLayoutPanel table, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var note = WrappingLabel(text, 10, UiTheme.Muted);
        note.Margin = new Padding(0, 0, 0, 10);
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(note, 1, row);
    }

    /// <summary>Retain native tab selection and keyboard navigation, with a clear selected state.</summary>
    internal sealed class EditorTabs : TabControl
    {
        private DpiAwareForm? _dpiHost;
        private bool _creatingHandle;

        public EditorTabs()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            Padding = new Point(18, 9);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= TabPages.Count) return;
            var selected = e.Index == SelectedIndex;
            var bounds = GetTabRect(e.Index);
            using var background = new SolidBrush(selected ? Color.White : UiTheme.Background);
            e.Graphics.FillRectangle(background, bounds);
            using var selectedFont = new Font(Font, selected ? FontStyle.Bold : FontStyle.Regular);
            var textBounds = Rectangle.Inflate(bounds, -UiTheme.Scale(this, 10), -UiTheme.Scale(this, 3));
            TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, selectedFont, textBounds,
                selected ? UiTheme.Blue : UiTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (selected)
            {
                using var accent = new SolidBrush(UiTheme.Blue);
                var height = UiTheme.Scale(this, 3);
                e.Graphics.FillRectangle(accent, bounds.Left + UiTheme.Scale(this, 8), bounds.Bottom - height,
                    Math.Max(1, bounds.Width - UiTheme.Scale(this, 16)), height);
                if (Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -UiTheme.Scale(this, 4), -UiTheme.Scale(this, 5)));
            }
        }

        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void CreateHandle()
        {
            _creatingHandle = true;
            try { base.CreateHandle(); }
            finally { _creatingHandle = false; }
            // TabControl.Padding recreates an existing HWND. Wait until the entire
            // native creation has returned, especially for pages first shown after
            // the host completed its initial DPI layout. Recreating in HandleCreated
            // destroys the HWND still being returned by CreateWindowEx (error 1400).
            UpdatePadding();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var host = FindForm() as DpiAwareForm;
            if (!ReferenceEquals(host, _dpiHost))
            {
                if (_dpiHost is not null) _dpiHost.DpiLayoutCompleted -= OnHostDpiLayoutCompleted;
                _dpiHost = host;
                if (_dpiHost is not null) _dpiHost.DpiLayoutCompleted += OnHostDpiLayoutCompleted;
            }
        }

        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdatePadding(); }
        private void OnHostDpiLayoutCompleted(object? sender, EventArgs e) => UpdatePadding();
        private void UpdatePadding()
        {
            if (_creatingHandle || !IsHandleCreated || IsDisposed || _dpiHost is { IsDpiLayoutReady: false }) return;
            // TabControl.Padding is a Point; the framework does not own this metric.
            var padding = new Point(UiTheme.Scale(this, 18), UiTheme.Scale(this, 9));
            if (Padding != padding) Padding = padding;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _dpiHost is not null) _dpiHost.DpiLayoutCompleted -= OnHostDpiLayoutCompleted;
            base.Dispose(disposing);
        }
    }

    internal sealed class FieldViewport : UserControl
    {
        private bool _initializing = true;
        private bool _arranging;
        public TableLayoutPanel Table { get; }

        public FieldViewport(int labelWidth = 168)
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("Microsoft YaHei UI", 10F);
            Dock = DockStyle.Fill; AutoScroll = true; BackColor = Color.White;
            Padding = new Padding(14); Margin = Padding.Empty;
            Table = Fields(labelWidth);
            Controls.Add(Table);
        }

        public void CompleteInitialization()
        {
            if (!_initializing) return;
            _initializing = false;
            ResumeLayout(false);
        }

        protected override void CreateHandle()
        {
            // Controls.Add can create this handle immediately in an already displayed
            // editor. Finish initial scaling before HandleCreated adjusts the font for
            // the destination monitor, including previously detached native tabs.
            CompleteInitialization();
            base.CreateHandle();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_initializing || _arranging || !IsHandleCreated || Table is null
                || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
            _arranging = true;
            try
            {
                var compact = ClientSize.Height < UiTheme.Scale(this, 250);
                var padding = UiTheme.Scale(this, new Padding(compact ? 8 : 14));
                if (Padding != padding) Padding = padding;
                var tablePadding = UiTheme.Scale(this, new Padding(compact ? 4 : 8));
                if (Table.Padding != tablePadding) Table.Padding = tablePadding;
                foreach (var input in Table.Controls.OfType<TextBox>().Where(x => x.Multiline))
                {
                    var available = ClientSize.Height - Padding.Vertical - Table.Padding.Vertical
                        - input.Margin.Vertical - UiTheme.Scale(this, 8);
                    var height = Math.Max(UiTheme.Scale(this, 36), Math.Min(UiTheme.Scale(this, 100), available));
                    if (input.Height != height) input.Height = height;
                }
            }
            finally { _arranging = false; }
        }
    }

    public static Label WrappingLabel(string text, float size = 10, Color? color = null, bool bold = false)
    {
        var label = UiTheme.Label(text, size, color, bold);
        label.Dock = DockStyle.Fill;
        return label;
    }

    public static void ConfigureDropDown(ComboBox input, int logicalWidth = 0, int logicalHeight = 320)
    {
        input.IntegralHeight = false;
        // These popup metrics are not scaled by ComboBox.ScaleControl. Compute them
        // from their logical baseline each time, including after moving to a new monitor.
        input.DropDown += (_, _) =>
        {
            var workingArea = Screen.FromControl(input).WorkingArea;
            var gap = UiTheme.Scale(input, 16);
            var desiredWidth = Math.Max(input.Width, UiTheme.Scale(input, logicalWidth));
            input.DropDownWidth = Math.Max(1, Math.Min(desiredWidth, workingArea.Width - gap));
            input.DropDownHeight = Math.Max(input.ItemHeight + 2,
                Math.Min(UiTheme.Scale(input, logicalHeight), workingArea.Height * 3 / 5));
        };
    }

    private sealed class FieldTable : TableLayoutPanel
    {
        private bool _updatingWidths;

        public FieldTable(int labelWidth)
        {
            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ColumnCount = 2;
            RowCount = 0;
            Padding = new Padding(8);
            Margin = Padding.Empty;
            MinimumSize = new Size(380, 0);
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            if (!_updatingWidths && ColumnStyles.Count > 0)
            {
                _updatingWidths = true;
                try
                {
                    foreach (var label in Controls.OfType<Label>())
                    {
                        var available = GetColumnSpan(label) > 1
                            ? ClientSize.Width - Padding.Horizontal
                            : GetColumn(label) == 0 ? (int)ColumnStyles[0].Width
                                : ClientSize.Width - Padding.Horizontal - (int)ColumnStyles[0].Width;
                        var maximum = new Size(Math.Max(1, available - label.Margin.Horizontal), 0);
                        if (label.MaximumSize != maximum) label.MaximumSize = maximum;
                    }
                }
                finally { _updatingWidths = false; }
            }
            base.OnLayout(levent);
        }
    }
}
