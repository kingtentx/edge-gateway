namespace EdgeGateway.WinForms;

internal sealed partial class DataRoutesDialog : EditorDialog
{
    internal string? Destination { get; private set; }
    public DataRoutesDialog() : base("数据去向", "已保存配置与当前快照；写入组统计不代表选中点位已入库。")
    {
        InitializeComponent(); SetReadOnlyView();
        realtime.Click += (_, _) => Navigate("realtime");
        history.Click += (_, _) => Navigate("history");
        logs.Click += (_, _) => Navigate("logs");
        database.Click += (_, _) => Navigate("database");
        diagnostics.Click += (_, _) => Navigate("diagnostics");
    }
    internal DataRoutesDialog(string title, string text) : this() { Text = title; details.Text = text; }
    private void Navigate(string destination) { Destination = destination; Close(); }
    protected override void Commit() => throw new InvalidOperationException("数据去向为只读快照。");
}
