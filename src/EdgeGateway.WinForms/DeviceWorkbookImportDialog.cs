using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal sealed class DeviceWorkbookImportDialog : EditorDialog
{
    private readonly ComboBox _mode;
    private readonly TextBox _errors;
    private readonly Label _summary;
    private readonly DataGridView _preview = UiTheme.Grid();
    private readonly Func<bool, DeviceWorkbookPreview> _readPreview;
    private DeviceWorkbookPreview _result = new([], 0, []);
    internal bool UpdateExisting => _mode.SelectedIndex == 1;

    public DeviceWorkbookImportDialog(Func<bool, DeviceWorkbookPreview> readPreview)
        : base("导入设备 Excel", "预览设备、通道及点位变更，确认后一次保存。")
    {
        _readPreview = readPreview;
        SetConfirmText("确认导入");
        var fields = AddTab("导入预览");
        Section(fields, "导入方式", "按标识更新会覆盖文件内设备的设置；未出现的设备、点位及驱动参数保留。");
        _mode = SelectInput(new object[] { "追加设备（标识重复将拒绝）", "按标识更新并追加" }, "追加设备（标识重复将拒绝）");
        _mode.Name = "DeviceWorkbookImportMode";
        Field(fields, "导入方式", _mode);
        _summary = EditorLayout.WrappingLabel("", 10, UiTheme.Ink, true);
        _summary.Padding = new Padding(12);
        AddWide(_summary);
        UiTheme.AddColumn(_preview, "Change", "变更", 80);
        UiTheme.AddColumn(_preview, "Name", "设备名称", 200, 180);
        UiTheme.AddColumn(_preview, "DriverId", "驱动", 180, 160);
        UiTheme.AddColumn(_preview, "TagCount", "保存后点位数", 140);
        _preview.Name = "DeviceWorkbookPreview";
        _preview.Height = 250;
        _preview.Dock = DockStyle.Top;
        AddWide(_preview);
        Section(fields, "校验结果");
        _errors = new TextBox { Name = "DeviceWorkbookErrors", Multiline = true, ReadOnly = true,
            Height = 120, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        UiTheme.StyleInput(_errors);
        _errors.AccessibleName = "设备工作簿校验结果";
        AddWide(_errors);
        Hint(fields, "新增时键和标识留空，点位按设备名称关联。通道和驱动参数在设备表中填写；导入新通道需停止全部设备。");
        _mode.SelectedIndexChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        void AddWide(Control control)
        {
            var row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.Controls.Add(control, 0, row);
            fields.SetColumnSpan(control, 2);
        }
    }

    private void RefreshPreview()
    {
        _result = _readPreview(UpdateExisting);
        _preview.DataSource = _result.Devices.ToList();
        _summary.Text = $"新增 {_result.Devices.Count(device => device.Change == "新增")} 台，更新 {_result.Devices.Count(device => device.Change == "更新")} 台；新增通道 {_result.NewChannels} 个。";
        _errors.Text = _result.Errors.Count == 0 ? "校验通过，可以确认导入。" : string.Join(Environment.NewLine, _result.Errors);
        _errors.ForeColor = _result.Errors.Count == 0 ? UiTheme.Green : UiTheme.Red;
    }

    protected override void Commit()
    {
        RefreshPreview();
        if (_result.Errors.Count > 0) throw new InvalidDataException("设备未导入：\n" + string.Join("\n", _result.Errors.Take(30)));
    }

    protected override Control? ResolveValidationField(Exception exception) =>
        exception is InvalidDataException ? _errors : base.ResolveValidationField(exception);
}
