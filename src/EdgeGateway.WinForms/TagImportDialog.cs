using EdgeGateway.Model;
using EdgeGateway.Service;
using System.Text.Json;

namespace EdgeGateway.WinForms;

/// <summary>A complete, non-mutating point-table preview before one atomic configuration application.</summary>
internal sealed class TagImportDialog : EditorDialog
{
    private readonly byte[] _content;
    private readonly IReadOnlyList<TagConfiguration> _existing;
    private readonly Func<IReadOnlyList<TagConfiguration>, IReadOnlyList<string>> _validate;
    private readonly ComboBox _mode;
    private readonly Label _summary;
    private readonly TextBox _errors;
    private readonly DataGridView _preview = UiTheme.Grid();
    private IReadOnlyList<string> _validationErrors = [];
    internal TagCsvImportResult Result { get; private set; } = null!;

    public TagImportDialog(string deviceName, byte[] content, IReadOnlyList<TagConfiguration> existing,
        Func<IReadOnlyList<TagConfiguration>, IReadOnlyList<string>> validate)
        : base("导入设备点位", "先预览点表与校验结果，再确认保存。追加会生成新点位标识；按名称更新会保留原标识和历史关联。任意错误都会拒绝整批导入。")
    {
        Text = "导入点位 · " + deviceName;
        _content = content; _existing = existing; _validate = validate;
        var fields = AddTab("导入预览");
        _mode = SelectInput(new object[] { "追加点位（重名将拒绝）", "按名称更新并追加（保留原标识）" }, "追加点位（重名将拒绝）");
        _mode.Name = "TagImportMode";
        Field(fields, "导入方式", _mode);
        _summary = EditorLayout.WrappingLabel("", 10, UiTheme.Ink, true);
        Field(fields, "变更摘要", _summary);
        UiTheme.AddColumn(_preview, "Change", "变更", 70, 70);
        UiTheme.AddColumn(_preview, "Name", "点位名称", 160, 160);
        UiTheme.AddColumn(_preview, "Address", "地址", 120);
        UiTheme.AddColumn(_preview, "DataType", "类型", 100);
        UiTheme.AddColumn(_preview, "Enabled", "采集", 65);
        UiTheme.AddColumn(_preview, "Upload", "上传", 65);
        UiTheme.AddColumn(_preview, "Method", "读取方法", 160, 160);
        UiTheme.AddColumn(_preview, "Scaling", "换算", 140, 140);
        UiTheme.AddColumn(_preview, "Expression", "计算表达式", 160, 160);
        UiTheme.AddColumn(_preview, "ScanInterval", "扫描周期", 100, 100);
        UiTheme.AddColumn(_preview, "Deadband", "上报死区", 100, 100);
        _preview.Name = "TagImportPreview";
        _preview.Height = 220;
        _preview.Dock = DockStyle.Top;
        var row = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(_preview, 0, row); fields.SetColumnSpan(_preview, 2);
        _errors = new TextBox
        {
            Name = "TagImportErrors", Multiline = true, ReadOnly = true, Height = 100,
            ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle
        };
        Field(fields, "校验结果", _errors);
        Hint(fields, "预览优先展示新增和变化点位，最多 500 行；保存使用完整点表。文件中未出现的原点位保留。协议地址、方法和类型须符合当前驱动。");
        _mode.SelectedIndexChanged += (_, _) => RefreshPreview();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        Result = TagCsvExchange.Import(_content, _existing,
            _mode.SelectedIndex == 1 ? TagCsvImportMode.UpdateByName : TagCsvImportMode.Append);
        _validationErrors = Result.IsValid ? _validate(Result.Tags) : Result.Errors.Select(x => x.ToString()).ToArray();
        _summary.Text = Result.IsValid
            ? $"新增 {Result.Added} 个，更新 {Result.Updated} 个；保存后共 {Result.TotalCount} 个点位。"
            : "文件包含错误，本次不会导入任何点位。";
        _errors.Text = _validationErrors.Count == 0 ? "校验通过，可以确认导入。" : string.Join(Environment.NewLine, _validationErrors);
        _errors.ForeColor = _validationErrors.Count == 0 ? UiTheme.Green : UiTheme.Red;
        var existing = _existing.ToDictionary(tag => tag.Id);
        _preview.DataSource = Result.Tags.Select(tag =>
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
    }

    protected override void Commit()
    {
        RefreshPreview();
        if (!Result.IsValid || _validationErrors.Count > 0)
            throw new InvalidDataException("点位未导入：\n" + string.Join("\n", _validationErrors.Take(30)));
    }

    internal void SelectUpdateModeForSmoke() => _mode.SelectedIndex = 1;
}
