namespace EdgeGateway.WinForms;

internal static class UiTheme
{
    public static readonly Color Navy = Color.FromArgb(17, 35, 60);
    public static readonly Color Blue = Color.FromArgb(37, 99, 235);
    public static readonly Color Background = Color.FromArgb(243, 246, 251);
    public static readonly Color Ink = Color.FromArgb(30, 41, 59);
    public static readonly Color Muted = Color.FromArgb(100, 116, 139);
    public static readonly Color Border = Color.FromArgb(221, 228, 238);
    public static readonly Color Green = Color.FromArgb(5, 150, 105);
    public static readonly Color Red = Color.FromArgb(220, 38, 38);

    public static Button Button(string text, EventHandler? action = null, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(98, 38), Height = 38,
            Padding = new Padding(12, 5, 12, 5), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Ink,
            Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = primary ? Blue : Border;
        if (action != null) button.Click += action;
        return button;
    }

    public static Label Label(string text, float size = 10, Color? color = null, bool bold = false) => new()
    {
        Text = text, AutoSize = true, ForeColor = color ?? Ink,
        Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0, 4, 0, 4), UseMnemonic = false
    };

    public static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            RowHeadersVisible = false, EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 42, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(238, 242, 247), RowTemplate = { Height = 36 },
            ShowCellErrors = true
        };
        grid.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9.5F);
        grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 234, 255);
        grid.DefaultCellStyle.SelectionForeColor = Ink;
        grid.DefaultCellStyle.Padding = new Padding(7, 4, 7, 4);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 254);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 241, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(65, 81, 107);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(7, 0, 7, 0);
        grid.DataError += (_, e) => e.ThrowException = false;
        return grid;
    }

    public static void AddColumn(DataGridView grid, string property, string caption, int minimumWidth = 90, float weight = 100) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = property, Name = property, HeaderText = caption,
            MinimumWidth = minimumWidth, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable
        });

    public static FlowLayoutPanel Toolbar(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true,
            Padding = new Padding(0, 8, 0, 10), Margin = new Padding(0)
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    public static TableLayoutPanel PageLayout(string title, string description, Control toolbar, Control content)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(22), BackColor = Background };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(Label(title, 19, Ink, true), 0, 0);
        panel.Controls.Add(Label(description, 10, Muted), 0, 1);
        panel.Controls.Add(toolbar, 0, 2);
        panel.Controls.Add(content, 0, 3);
        return panel;
    }
}
