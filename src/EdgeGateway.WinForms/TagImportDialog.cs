using EdgeGateway.Model;
using EdgeGateway.Application;
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
    internal TagCsvImportMode Mode => _mode.SelectedIndex == 1 ? TagCsvImportMode.UpdateByName : TagCsvImportMode.Append;
    internal TagCsvImportResult Result { get; private set; } = null!;

    public TagImportDialog(string deviceName, byte[] content, IReadOnlyList<TagConfiguration> existing,
        Func<IReadOnlyList<TagConfiguration>, IReadOnlyList<string>> validate)
        : base("导入设备点位", "选择导入方式，核对变更与校验结果后一次保存。")
    {
        Text = "导入点位 · " + deviceName;
        _content = content; _existing = existing; _validate = validate;
        SetConfirmText("确认导入");
        var fields = AddTab("导入预览");
        Section(fields, "导入方式", "按名称更新可保留原标识与历史关联；任意错误都会拒绝整批导入。");
        _mode = SelectInput(new object[] { "追加点位（重名将拒绝）", "按名称更新并追加（保留原标识）" }, "追加点位（重名将拒绝）");
        _mode.Name = "TagImportMode";
        Field(fields, "导入方式", _mode);
        _summary = EditorLayout.WrappingLabel("", 10, UiTheme.Ink, true);
        _summary.Name = "TagImportSummary";
        _summary.Padding = new Padding(12, 10, 12, 10);
        _summary.Margin = new Padding(0, 8, 0, 8);
        var summaryRow = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(_summary, 0, summaryRow); fields.SetColumnSpan(_summary, 2);
        Section(fields, "点位预览", "优先显示新增与更新点位，最多 500 行。");
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
        Section(fields, "校验结果");
        UiTheme.StyleInput(_errors);
        _errors.AccessibleName = "点表校验结果";
        row = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(_errors, 0, row); fields.SetColumnSpan(_errors, 2);
        Hint(fields, "保存使用完整点表；文件中未出现的原点位保留。协议地址、方法和类型须符合当前驱动。");
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

    protected override Control? ResolveValidationField(Exception exception) =>
        exception is InvalidDataException && _validationErrors.Count > 0 ? _errors : base.ResolveValidationField(exception);

    internal void SelectUpdateModeForSmoke() => _mode.SelectedIndex = 1;
}
