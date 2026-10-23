using FontAwesome.Sharp;

namespace EdgeGateway.WinForms;

internal static class UiTheme
{
    public static readonly Color Navy = Color.FromArgb(15, 27, 47);
    public static readonly Color Blue = Color.FromArgb(39, 102, 232);
    public static readonly Color Background = Color.FromArgb(243, 246, 251);
    public static readonly Color Ink = Color.FromArgb(24, 38, 62);
    public static readonly Color Muted = Color.FromArgb(102, 118, 139);
    public static readonly Color Border = Color.FromArgb(223, 230, 240);
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
            Text = text, AutoSize = true, MinimumSize = new Size(108, 42), Height = 42,
            Padding = new Padding(12, 7, 12, 7), BackColor = primary ? Blue : Color.White,
            ForeColor = primary ? Color.White : Ink, Margin = new Padding(0, 0, 8, 0),
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
            "cloud" => IconChar.Cloud, "logs" => IconChar.ListAlt, _ => IconChar.Circle
        }, Cursor = Cursors.Hand
    };

    public static TableLayoutPanel Card(Color? accent = null) => new SurfaceCard { Accent = accent ?? Blue, BackColor = Color.White };

    private static IconChar ActionIcon(string text) => text switch
    {
        "重置修改" => IconChar.SyncAlt,
        "确定" => IconChar.Check, "取消" => IconChar.Times,
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
            Padding = new Padding(0, 10, 0, 12), Margin = new Padding(0)
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    public static Control PageLayout(string title, string description, Control toolbar, Control content)
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 4, Padding = new Padding(24), BackColor = Background,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = Label(title, 21, Ink, true);
        var subtitle = Label(description, 10, Muted);
        heading.Dock = subtitle.Dock = DockStyle.Fill;
        panel.Controls.Add(heading, 0, 0); panel.Controls.Add(subtitle, 0, 1);
        panel.Controls.Add(toolbar, 0, 2); panel.Controls.Add(content, 0, 3);
        panel.SizeChanged += (_, _) =>
        {
            var width = Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal);
            heading.MaximumSize = new Size(width, 0); subtitle.MaximumSize = new Size(width, 0);
        };
        return new PageViewport(panel) { Name = "PageViewport_" + title };
    }
}
