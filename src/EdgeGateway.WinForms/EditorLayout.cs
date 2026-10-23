namespace EdgeGateway.WinForms;

/// <summary>Layout rules shared by the editor forms and newly created driver fields.</summary>
internal static class EditorLayout
{
    public static TableLayoutPanel Fields(int labelWidth = 155) => new FieldTable(labelWidth);

    internal sealed class FieldViewport : UserControl
    {
        private bool _initializing = true;
        private bool _arranging;
        public TableLayoutPanel Table { get; }

        public FieldViewport(int labelWidth = 155)
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
            if (_initializing || _arranging || !IsHandleCreated || Table is null) return;
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
                            : (int)ColumnStyles[0].Width;
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
