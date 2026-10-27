using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal sealed partial class DeviceWorkbookImportDialog : EditorDialog
{
    private readonly Func<bool, DeviceWorkbookPreview> _readPreview;
    private DeviceWorkbookPreview _result = new([], 0, []);
    internal bool UpdateExisting => _mode.SelectedIndex == 1;

    public DeviceWorkbookImportDialog() : base("导入设备 Excel", "预览并确认导入。")
    { _readPreview = _ => new([], 0, []); InitializeComponent(); }

    public DeviceWorkbookImportDialog(Func<bool, DeviceWorkbookPreview> readPreview)
        : base("导入设备 Excel", "预览设备、通道及点位变更，确认后一次保存。")
    {
        _readPreview = readPreview;
        SetConfirmText("确认导入");
        InitializeComponent();
        _mode.Items.AddRange(new object[] { "追加设备（标识重复将拒绝）", "按标识更新并追加" });
        _mode.SelectedIndex = 0;
        _mode.SelectedIndexChanged += (_, _) => RefreshPreview();
        RefreshPreview();
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
