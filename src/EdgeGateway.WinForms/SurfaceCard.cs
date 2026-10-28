namespace EdgeGateway.WinForms;

/// <summary>Official Panel surface; the inner native table is layout only.</summary>
internal sealed partial class SurfaceCard : AntdUI.Panel
{
    public SurfaceCard() => InitializeComponent();
    public Color Accent { get; set; } = UiTheme.Blue;
    public AutoSizeMode AutoSizeMode { get => content.AutoSizeMode; set => content.AutoSizeMode = value; }
    public int ColumnCount { get => content.ColumnCount; set => content.ColumnCount = value; }
    public int RowCount { get => content.RowCount; set => content.RowCount = value; }
    public TableLayoutColumnStyleCollection ColumnStyles => content.ColumnStyles;
    public TableLayoutRowStyleCollection RowStyles => content.RowStyles;
    public new TableLayoutControlCollection Controls => content.Controls;
    public void SetColumnSpan(Control child, int span) => content.SetColumnSpan(child, span);
    public void SetRowSpan(Control child, int span) => content.SetRowSpan(child, span);
    public Control? GetControlFromPosition(int column, int row) => content.GetControlFromPosition(column, row);
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (content is null) return base.GetPreferredSize(proposedSize);
        var size = content.GetPreferredSize(new Size(Math.Max(1, proposedSize.Width - Padding.Horizontal), proposedSize.Height));
        return new Size(size.Width + Padding.Horizontal, size.Height + Padding.Vertical);
    }
}
