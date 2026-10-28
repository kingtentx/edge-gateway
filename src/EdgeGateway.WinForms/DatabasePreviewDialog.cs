using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal sealed partial class DatabasePreviewDialog : EditorDialog
{
    public DatabasePreviewDialog() : base("数据库检查与预览", "只读检查字段与采样映射，不创建表或写入数据。")
    { InitializeComponent(); GridPagination.Attach(valuesGrid, valuesGridPagination); SetReadOnlyView(); }

    internal DatabasePreviewDialog(DatabaseWritePreview preview) : this()
    {
        schemaState.Text = preview.SchemaValid ? "字段结构检查通过" : "字段结构存在问题";
        rowState.Text = preview.RowReady ? "当前快照可以形成待写入行；这不是数据库提交成功证明。"
            : "当前不能形成可写行，请检查字段、点位启用状态、设备运行状态与采样时效。";
        observedAt.Text = "检查时间：" + preview.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
        messages.Text = string.Join(Environment.NewLine, preview.Errors.Select(error => "问题：" + error).Concat(preview.Warnings));
        GridViewState.Rebind(valuesGrid, preview.Columns, row => row.Column);
    }
    protected override void Commit() => throw new InvalidOperationException("预览为只读窗口。");
}
