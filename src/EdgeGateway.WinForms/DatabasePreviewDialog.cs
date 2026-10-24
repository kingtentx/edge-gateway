using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal sealed class DatabasePreviewDialog : EditorDialog
{
    internal DatabasePreviewDialog(DatabaseWritePreview preview) : base("数据库检查与预览", "只读检查目标字段及当前采样映射；不会创建表、插入数据或修改设备状态。")
    {
        SetReadOnlyView();
        var summary = AddTab("检查结果");
        Section(summary, preview.SchemaValid ? "字段结构检查通过" : "字段结构存在问题",
            preview.RowReady ? "当前快照可以形成待写入行；这不是数据库提交成功证明。" : "当前不能形成可写行，请检查字段、点位启用状态、设备运行状态与采样时效。");
        Hint(summary, "检查时间：" + preview.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
        foreach (var error in preview.Errors) Hint(summary, "问题：" + error);
        foreach (var warning in preview.Warnings) Hint(summary, warning);
        var page = new TabPage("字段与值") { BackColor = Color.White };
        var grid = UiTheme.Grid(); grid.Name = "DatabasePreviewValues";
        UiTheme.AddColumn(grid, "Column", "目标字段", 150, 130); UiTheme.AddColumn(grid, "DatabaseType", "数据库类型", 130);
        UiTheme.AddColumn(grid, "Source", "来源 / 转换", 230, 180); UiTheme.AddColumn(grid, "Value", "转换后值", 260, 200);
        UiTheme.AddColumn(grid, "SampleTime", "源采样时间", 190, 190); UiTheme.AddColumn(grid, "Error", "问题", 280, 220);
        grid.DataSource = preview.Columns.ToArray(); page.Controls.Add(grid); Tabs.TabPages.Add(page);
    }
    protected override void Commit() => throw new InvalidOperationException("预览为只读窗口。");
}
