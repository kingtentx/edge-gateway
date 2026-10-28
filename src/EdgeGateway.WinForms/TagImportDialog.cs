using EdgeGateway.Model;
using EdgeGateway.Application;
using System.Text.Json;

namespace EdgeGateway.WinForms;

/// <summary>A complete, non-mutating point-table preview before one atomic configuration application.</summary>
internal sealed partial class TagImportDialog : EditorDialog
{
    private readonly byte[] _content;
    private readonly IReadOnlyList<TagConfiguration> _existing;
    private readonly Func<IReadOnlyList<TagConfiguration>, IReadOnlyList<string>> _validate;
    private IReadOnlyList<string> _validationErrors = [];
    internal TagCsvImportMode Mode => _mode.SelectedIndex == 1 ? TagCsvImportMode.UpdateByName : TagCsvImportMode.Append;
    internal TagCsvImportResult Result { get; private set; } = null!;

    public TagImportDialog() : base("导入设备点位", "预览并确认导入。")
    { _content = []; _existing = []; _validate = _ => []; InitializeComponent(); GridPagination.Attach(_preview, _previewPagination); }

    public TagImportDialog(string deviceName, byte[] content, IReadOnlyList<TagConfiguration> existing,
        Func<IReadOnlyList<TagConfiguration>, IReadOnlyList<string>> validate)
        : base("导入设备点位", "选择导入方式，核对变更与校验结果后一次保存。")
    {
        Text = "导入点位 · " + deviceName;
        _content = content; _existing = existing; _validate = validate;
        SetConfirmText("确认导入");
        InitializeComponent(); GridPagination.Attach(_preview, _previewPagination);
        _mode.Items.AddRange(new object[] { "追加点位（重名将拒绝）", "按名称更新并追加（保留原标识）" });
        _mode.SelectedIndex = 0;
        _mode.SelectedIndexChanged += (_, _) => RefreshPreview();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        Result = TagCsvExchange.Import(_content, _existing,
            Mode);
        _validationErrors = Result.IsValid ? _validate(Result.Tags) : Result.Errors.Select(x => x.ToString()).ToArray();
        _summary.Text = Result.IsValid && _validationErrors.Count == 0
            ? $"新增 {Result.Added} 个，更新 {Result.Updated} 个；保存后共 {Result.TotalCount} 个点位。"
            : "发现校验问题，本次尚未导入任何点位。";
        _summary.BackColor = _validationErrors.Count == 0 ? Color.FromArgb(236, 248, 243) : Color.FromArgb(255, 245, 245);
        _summary.ForeColor = _validationErrors.Count == 0 ? UiTheme.Green : UiTheme.Red;
        _errors.Text = _validationErrors.Count == 0 ? "校验通过，可以确认导入。" : string.Join(Environment.NewLine, _validationErrors);
        _errors.ForeColor = _validationErrors.Count == 0 ? UiTheme.Green : UiTheme.Red;
        var existing = _existing.ToDictionary(tag => tag.Id);
        var rows = Result.Tags.Select(tag =>
        {
            var change = !existing.TryGetValue(tag.Id, out var previous) ? 0
                : JsonSerializer.Serialize(tag) != JsonSerializer.Serialize(previous) ? 1 : 2;
            return (Tag: tag, Change: change);
        }).OrderBy(row => row.Change).Take(500).Select(row => new
        {
            Change = row.Change == 0 ? "新增" : row.Change == 1 ? "更新" : "保留",
            row.Tag.Name, row.Tag.Address, DataType = row.Tag.DataType.ToString(), Enabled = row.Tag.Enabled ? "是" : "否",
            Upload = row.Tag.UploadEnabled ? "是" : "否", Method = row.Tag.ReadMethod, Scaling = $"× {row.Tag.Scale:G6} + {row.Tag.Offset:G6}",
            row.Tag.Expression, ScanInterval = row.Tag.ScanIntervalMs == 0 ? "继承设备" : $"{row.Tag.ScanIntervalMs} ms",
            Deadband = row.Tag.Deadband.ToString("G", System.Globalization.CultureInfo.InvariantCulture)
        }).ToList();
        GridViewState.Rebind(_preview, rows, row => row.Name);
    }

    protected override void Commit()
    {
        RefreshPreview();
        if (!Result.IsValid || _validationErrors.Count > 0)
            throw new InvalidDataException("点位未导入：\n" + string.Join("\n", _validationErrors.Take(30)));
    }

    protected override Control? ResolveValidationField(Exception exception) =>
        exception is InvalidDataException && _validationErrors.Count > 0 ? _errors : base.ResolveValidationField(exception);

    internal void SelectUpdateModeForSmoke() => _mode.SelectedIndex = 1;
}
