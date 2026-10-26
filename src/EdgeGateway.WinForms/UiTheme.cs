using FontAwesome.Sharp;

namespace EdgeGateway.WinForms;

internal static class UiTheme
{
    public const int ActionHeight = 38;
    public static readonly Color Navy = Color.FromArgb(18, 31, 51);
    public static readonly Color Blue = Color.FromArgb(37, 99, 235);
    public static readonly Color Background = Color.FromArgb(244, 247, 251);
    public static readonly Color Ink = Color.FromArgb(28, 43, 63);
    public static readonly Color Muted = Color.FromArgb(102, 118, 139);
    public static readonly Color Border = Color.FromArgb(221, 228, 237);
    public static readonly Color InputBackground = Color.FromArgb(249, 251, 254);
    public static readonly Color BlueSoft = Color.FromArgb(235, 242, 255);
    public static readonly Color Warning = Color.FromArgb(164, 93, 16);
    public static readonly Color Green = Color.FromArgb(13, 155, 124);
    public static readonly Color Red = Color.FromArgb(216, 62, 75);
    public static int Scale(Control control, int logical)
    {
        // Construction and the framework's scale transaction still operate on design
        // values. Responsive code must not pre-scale constants that .NET will scale again.
        var form = control.FindForm();
        var dpi = form is DpiAwareForm { IsDpiLayoutReady: false } || form is null ? 96 : control.DeviceDpi;
        return DpiLayoutMath.Scale(logical, dpi);
    }
    public static Padding Scale(Control control, Padding logical) => new(Scale(control, logical.Left),
        Scale(control, logical.Top), Scale(control, logical.Right), Scale(control, logical.Bottom));

    public static Button Button(string text, EventHandler? action = null, bool primary = false)
    {
        var button = new ModernButton
        {
            Text = text, AutoSize = true, MinimumSize = new Size(100, ActionHeight), Height = ActionHeight,
            Padding = new Padding(13, 6, 13, 6), BackColor = primary ? Blue : Color.White,
            ForeColor = primary ? Color.White : text.Contains("删除") ? Red : Ink,
            Margin = new Padding(0, 0, 8, 6), AccessibleName = text,
            Cursor = Cursors.Hand, Glyph = ActionIcon(text), Primary = primary
        };
        if (action != null) button.Click += action;
        return button;
    }

    public static Button NavigationButton(string text, string key) => new ModernButton
    {
        Text = text, BackColor = Navy, ForeColor = Color.FromArgb(184, 201, 224),
        Padding = new Padding(14, 10, 10, 10), MinimumSize = new Size(0, 48),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), Navigation = true,
        Glyph = key switch
        {
            "overview" => IconChar.TachometerAlt, "devices" => IconChar.NetworkWired,
            "realtime" => IconChar.WaveSquare, "history" => IconChar.ChartLine,
            "database" => IconChar.Database, "cloud" => IconChar.Cloud, "logs" => IconChar.ListAlt, "diagnostics" => IconChar.Plug, _ => IconChar.Circle
        }, Cursor = Cursors.Hand
    };

    public static TableLayoutPanel Card(Color? accent = null) => new SurfaceCard { Accent = accent ?? Blue, BackColor = Color.White };

    internal static IconChar ActionIcon(string text) => text switch
    {
        "重置修改" => IconChar.SyncAlt,
        "确定" or "保存配置" or "保存并应用" or "确认导入" => IconChar.Check, "取消" => IconChar.Times,
        "清除筛选" => IconChar.Filter,
        "通道管理" => IconChar.NetworkWired,
        "连接诊断" or "单点读取" => IconChar.Plug,
        _ when text.Contains("更多") => IconChar.EllipsisH,
        _ when text.Contains("复制") => IconChar.Copy,
        _ when text.Contains("添加") || text.Contains("新增") => IconChar.Plus,
        _ when text.Contains("编辑") || text.Contains("修改") => IconChar.Pen,
        _ when text.Contains("删除") => IconChar.TrashAlt,
        _ when text.Contains("导出") => IconChar.FileExport,
        _ when text.Contains("导入") => IconChar.FileImport,
        _ when text.Contains("查询") => IconChar.Search,
        _ when text.Contains("刷新") => IconChar.SyncAlt,
        _ when text.Contains("写入") => IconChar.Pen,
        _ when text.Contains("应用") => IconChar.Check,
        _ => IconChar.None
    };

    /// <summary>Styles native inputs in place, preserving their keyboard and DPI behavior.</summary>
    public static void StyleInput(Control input)
    {
        input.ForeColor = Ink;
        if (input is TextBoxBase text)
        {
            text.BorderStyle = BorderStyle.FixedSingle;
            text.BackColor = text.ReadOnly ? Color.FromArgb(242, 245, 249) : InputBackground;
            if (!text.Multiline) text.MinimumSize = new Size(text.MinimumSize.Width, 30);
        }
        else if (input is ComboBox combo)
        {
            combo.BackColor = InputBackground;
            combo.FlatStyle = FlatStyle.Flat;
        }
        else if (input is NumericUpDown number)
        {
            number.BorderStyle = BorderStyle.FixedSingle;
            number.BackColor = InputBackground;
            number.MinimumSize = new Size(number.MinimumSize.Width, 30);
        }
        else if (input is DateTimePicker picker) picker.CalendarForeColor = Ink;
        else if (input is CheckBox check) check.MinimumSize = new Size(check.MinimumSize.Width, 32);
    }

    public static Control InputSurface(Control input)
    {
        if (input.Parent is InputFrame existing) return existing;
        if (input is not (TextBox { Multiline: false } or ComboBox or NumericUpDown or DateTimePicker)) return input;
        StyleInput(input);
        return new InputFrame(input);
    }

    public static Control FilterField(string caption, Control input)
    {
        StyleInput(input);
        var group = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(0, 0, 12, 6), Padding = Padding.Empty
        };
        group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = Label(caption, 10, Muted);
        label.Margin = new Padding(0, 0, 0, 5);
        input.Margin = Padding.Empty;
        input.AccessibleName = caption;
        input = InputSurface(input);
        group.Controls.Add(label, 0, 0); group.Controls.Add(input, 0, 1);
        var measuring = false;
        void MeasureInputRow()
        {
            if (measuring || group.IsDisposed || input.IsDisposed
                || group.FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
            measuring = true;
            try
            {
                // Native fixed-height inputs can report a smaller specified height
                // than their MinimumSize-clamped bounds. Reserve their full current
                // pixel height; the form owns DPI scaling, so do not scale it again.
                var height = Math.Max(input.Height, Math.Max(input.MinimumSize.Height,
                    input.GetPreferredSize(new Size(input.Width, 0)).Height)) + input.Margin.Vertical;
                var row = group.RowStyles[1];
                if (row.SizeType != SizeType.Absolute) row.SizeType = SizeType.Absolute;
                if (row.Height != height) row.Height = height;
            }
            finally { measuring = false; }
        }
        group.Layout += (_, _) => MeasureInputRow();
        input.SizeChanged += (_, _) => MeasureInputRow();
        input.FontChanged += (_, _) => MeasureInputRow();
        MeasureInputRow();
        return group;
    }

    public static Label Label(string text, float size = 10, Color? color = null, bool bold = false) => new()
    {
        Text = text, AutoSize = true, ForeColor = color ?? Ink,
        Font = new Font("Microsoft YaHei UI", Math.Max(10F, size), bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0, 4, 0, 4), UseMnemonic = false
    };

    public static DataGridView Grid()
    {
        var grid = new DpiDataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            RowHeadersVisible = false, EnableHeadersVisualStyles = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(239, 243, 249), ShowCellErrors = true
        };
        grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(228, 238, 255);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 77, 155);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 252, 255);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(237, 243, 251);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(76, 94, 119);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = grid.ColumnHeadersDefaultCellStyle.ForeColor;
        grid.DataError += (_, e) => e.ThrowException = false;
        return grid;
    }

    public static void AddColumn(DataGridView grid, string property, string caption, int minimumWidth = 90, float weight = 100)
    {
        var column = new DataGridViewTextBoxColumn
        {
            DataPropertyName = property, Name = property, HeaderText = caption,
            MinimumWidth = minimumWidth, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable
        };
        grid.Columns.Add(column);
        if (grid is DpiDataGridView dpiGrid) dpiGrid.RegisterColumn(column, minimumWidth);
    }

    public static FlowLayoutPanel Toolbar(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 4), Margin = new Padding(0)
        };
        panel.Controls.AddRange(controls.Select(control =>
        {
            var surface = InputSurface(control);
            if (surface is InputFrame) surface.Margin = new Padding(0, 0, 8, 6);
            return surface;
        }).ToArray());
        if (controls.Length == 0) panel.Padding = Padding.Empty;
        return panel;
    }

    public static Control PageLayout(string title, string description, Control toolbar, Control content)
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 4, Padding = new Padding(20), BackColor = Background,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = Label(title, 21, Ink, true);
        var hasDescription = !string.IsNullOrWhiteSpace(description);
        var subtitle = Label(description, 10, Muted);
        subtitle.Visible = hasDescription;
        heading.Dock = subtitle.Dock = DockStyle.Fill;
        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var help = new LinkLabel
        {
            Text = "说明", AutoSize = true, Visible = false, Anchor = AnchorStyles.Right,
            LinkColor = Blue, ActiveLinkColor = Blue, Margin = new Padding(10, 5, 0, 0),
            AccessibleName = title + "页面说明"
        };
        help.LinkClicked += (_, _) => MessageBox.Show(panel.FindForm(), description, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        titleRow.Controls.Add(heading, 0, 0); titleRow.Controls.Add(help, 1, 0);
        panel.Controls.Add(titleRow, 0, 0); panel.Controls.Add(subtitle, 0, 1);
        panel.Controls.Add(toolbar, 0, 2); panel.Controls.Add(content, 0, 3);
        void ArrangePage()
        {
            if (panel.FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
            var compact = panel.FindForm()?.ClientSize.Height < Scale(panel, 620);
            var shortViewport = panel.FindForm()?.ClientSize.Height < Scale(panel, 480);
            panel.Padding = Scale(panel, compact ? new Padding(14, 8, 14, 12) : new Padding(22, 16, 22, 18));
            var width = Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal);
            subtitle.Visible = hasDescription && !shortViewport; help.Visible = hasDescription && shortViewport;
            heading.MaximumSize = new Size(Math.Max(1, width - (help.Visible ? help.PreferredWidth + help.Margin.Horizontal : 0)), 0);
            subtitle.MaximumSize = new Size(width, 0);
            heading.Margin = Scale(panel, new Padding(0, 0, 0, compact ? 2 : 4));
            subtitle.Margin = Scale(panel, new Padding(0, 0, 0, compact ? 2 : 6));
        }
        panel.SizeChanged += (_, _) => ArrangePage();
        panel.DpiChangedAfterParent += (_, _) => ArrangePage();
        return new PageViewport(panel) { Name = "PageViewport_" + title };
    }
}
