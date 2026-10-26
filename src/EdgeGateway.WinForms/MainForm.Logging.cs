using EdgeGateway.Model;
using System.Diagnostics;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly TabControl _logTabs = new EditorLayout.EditorTabs { Name = "LoggingTabs", Dock = DockStyle.Fill, Multiline = true };
    private readonly Dictionary<string, Control> _loggingFields = [];
    private readonly Label _loggingState = UiTheme.Label("日志设置已保存。", 10, UiTheme.Muted);
    // Keep the status scrollable instead of an unbounded wrapping label: at the
    // smallest supported work area the settings viewport is only 58 logical pixels high.
    private readonly TextBox _loggingFileState = new()
    {
        Multiline = true, ReadOnly = true, AutoSize = false, Height = 48,
        ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top,
        BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = UiTheme.Muted,
        AccessibleName = "日志文件状态，可滚动查看并复制完整内容"
    };
    private Button _loggingApply = null!, _loggingReset = null!;
    private bool _loggingDirty, _bindingLogging;

    private Control WithLoggingSettings()
    {
        var settingsTab = new TabPage("日志设置") { BackColor = Color.White };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var viewport = new Panel { Name = "LoggingFieldsViewport", Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), Margin = Padding.Empty };
        var fields = EditorLayout.Fields(); viewport.Controls.Add(fields);
        void Add(string key, string label, Control control)
        { control.Name = "Logging_" + key; EditorDialog.Field(fields, label, control); _loggingFields.Add(key, control); _configurationCommands.Add(control); }
        void Changed() { if (_bindingLogging) return; _loggingDirty = true; UpdateCommands(); }
        var level = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FormattingEnabled = true }; level.Items.AddRange(Enum.GetValues<GatewayLogLevel>().Cast<object>().ToArray());
        level.Format += (_, e) => { if (e.ListItem is GatewayLogLevel value) e.Value = LogLevelText(value); }; level.SelectedIndexChanged += (_, _) => Changed();
        var samples = new CheckBox { Text = "将每个采样结果写入采样日志", AutoSize = true }; samples.CheckedChanged += (_, _) => Changed();
        EditorDialog.Section(fields, "运行日志", "保存系统事件、设备状态、故障与操作记录；最低级别不影响采样数据。");
        Add("MinimumLevel", "运行日志最低级别", level);
        void Number(string key, string label, int min, int max)
        { var input = new NumericUpDown { Minimum = min, Maximum = max, ThousandsSeparator = true }; input.ValueChanged += (_, _) => Changed(); Add(key, label, input); }
        Number("MaxFileSizeMb", "单文件最大大小（M）", 1, 1024); Number("RetainedFileCount", "最多保留文件数", 1, 1000); Number("RetainedDays", "最多保留天数", 1, 3650);
        EditorDialog.Section(fields, "采样数据", "独立记录逐点原始值、工程值、质量、时间和错误；关闭后仍可查询已保存的文件。");
        Add("Samples", "记录采样数据", samples);
        Number("SampleMaxFileSizeMb", "单文件最大大小（M）", 1, 1024);
        Number("SampleRetainedFileCount", "最多保留文件数", 1, 1000);
        Number("SampleRetainedDays", "最多保留天数", 1, 3650);
        EditorDialog.Section(fields, "磁盘保护", "低于保护阈值暂停文件写入；空间达到阈值加恢复余量后自动恢复。保护期间的日志不补写，丢弃数可见。不会删除其他业务文件。");
        Number("MinimumFreeDiskSpaceMb", "最低剩余空间（M）", 16, 1048576);
        Number("DiskResumeMarginMb", "恢复余量（M）", 1, 65536);
        var logPaths = _application.GetLoggingStatus();
        EditorDialog.Hint(fields, "程序日志：" + (logPaths.DirectoryPath ?? "未启用文件日志") + Environment.NewLine
            + "点位采样日志：" + (logPaths.SampleDirectoryPath ?? "未启用文件日志") + Environment.NewLine
            + "在“运行日志”和“采样数据”页签分别查询并导出已保存的记录。");
        var openLogs = UiTheme.Button("打开日志目录", (_, _) =>
        {
            try
            {
                var directory = _application.GetLoggingStatus().DirectoryPath;
                if (directory is null) throw new InvalidOperationException("当前宿主未启用文件日志。");
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception exception) { ShowError(exception.Message); }
        });
        _loggingFileState.Name = "LoggingFileStatus"; DatabaseConnectionDialog.Span(fields, _loggingFileState);
        openLogs.Name = "OpenLogDirectory"; DatabaseConnectionDialog.Span(fields, openLogs);
        _loggingApply = UiTheme.Button("应用日志设置", async (_, _) => await RunBusyAsync(ApplyLoggingSettingsAsync), true); _loggingApply.Name = "ApplyLoggingSettings";
        _loggingReset = UiTheme.Button("重置日志设置", (_, _) => { _loggingDirty = false; BindLoggingFields(); UpdateCommands(); }); _loggingReset.Name = "ResetLoggingSettings";
        // A GrowOnly AutoSize footer retains its default 100-pixel height and can
        // squeeze the field viewport below one full button on short work areas.
        var footer = new TableLayoutPanel { Name = "LoggingApplyBar", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 8, 8, 4), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _loggingState.Name = "LoggingDraftState"; _loggingState.Dock = DockStyle.Fill; footer.Controls.Add(_loggingState, 0, 0);
        var actions = UiTheme.Toolbar(_loggingApply, _loggingReset); actions.Padding = Padding.Empty;
        footer.Controls.Add(actions, 0, 1);
        footer.SizeChanged += (_, _) => SetWrappingWidth(_loggingState, footer.ClientSize.Width - footer.Padding.Horizontal);
        content.Controls.Add(viewport, 0, 0); content.Controls.Add(footer, 0, 1); settingsTab.Controls.Add(content);
        _logTabs.TabPages.Add(settingsTab);
        _logTabs.TabPages.Add(BuildLogArchivePage(false)); _logTabs.TabPages.Add(BuildLogArchivePage(true));
        return _logTabs;
    }
    private void BindLoggingFields()
    {
        if (_loggingDirty || _loggingFields.Count == 0) return;
        _bindingLogging = true;
        try
        {
            var settings = _configuration.Logging;
            ((ComboBox)_loggingFields["MinimumLevel"]).SelectedItem = settings.MinimumLevel;
            ((CheckBox)_loggingFields["Samples"]).Checked = settings.LogAcquisitionSamples;
            foreach (var (key, value) in new[] { ("MaxFileSizeMb", settings.MaxFileSizeMb), ("RetainedFileCount", settings.RetainedFileCount), ("RetainedDays", settings.RetainedDays), ("SampleMaxFileSizeMb", settings.SampleMaxFileSizeMb), ("SampleRetainedFileCount", settings.SampleRetainedFileCount), ("SampleRetainedDays", settings.SampleRetainedDays), ("MinimumFreeDiskSpaceMb", settings.MinimumFreeDiskSpaceMb), ("DiskResumeMarginMb", settings.DiskResumeMarginMb) })
            { var input = (NumericUpDown)_loggingFields[key]; input.Value = Math.Clamp(value, (int)input.Minimum, (int)input.Maximum); }
        }
        finally { _bindingLogging = false; }
    }
    private GatewayLoggingConfiguration CaptureLoggingSettings() => new()
    {
        MinimumLevel = (GatewayLogLevel)((ComboBox)_loggingFields["MinimumLevel"]).SelectedItem!,
        LogAcquisitionSamples = ((CheckBox)_loggingFields["Samples"]).Checked,
        MaxFileSizeMb = (int)((NumericUpDown)_loggingFields["MaxFileSizeMb"]).Value,
        RetainedFileCount = (int)((NumericUpDown)_loggingFields["RetainedFileCount"]).Value,
        RetainedDays = (int)((NumericUpDown)_loggingFields["RetainedDays"]).Value,
        SampleMaxFileSizeMb = (int)((NumericUpDown)_loggingFields["SampleMaxFileSizeMb"]).Value,
        SampleRetainedFileCount = (int)((NumericUpDown)_loggingFields["SampleRetainedFileCount"]).Value,
        SampleRetainedDays = (int)((NumericUpDown)_loggingFields["SampleRetainedDays"]).Value,
        MinimumFreeDiskSpaceMb = (int)((NumericUpDown)_loggingFields["MinimumFreeDiskSpaceMb"]).Value,
        DiskResumeMarginMb = (int)((NumericUpDown)_loggingFields["DiskResumeMarginMb"]).Value
    };
    private async Task ApplyLoggingSettingsAsync()
    {
        if (!_loggingDirty) return;
        var applied = await _application.ApplyLoggingSettingsAsync(CaptureLoggingSettings());
        _loggingDirty = false; CompleteConfigurationCommit(applied, successMessage: "日志设置已保存并应用，设备继续采集。");
    }
    private void UpdateLoggingCommands()
    {
        if (_loggingApply is null) return;
        _loggingApply.Enabled = CanEdit() && _loggingDirty; _loggingReset.Enabled = CanEdit() && _loggingDirty;
        _loggingState.Text = _loggingDirty ? "有未保存的修改。应用后立即生效，正在采集的设备继续运行。" : "日志设置已保存；可在设备运行期间调整。";
        _loggingState.ForeColor = _loggingDirty ? UiTheme.Warning : UiTheme.Muted;
        var status = _application.GetLoggingStatus();
        _loggingFileState.Text = $"日志队列：{status.PendingEntries:N0} 条；累计丢弃：{status.DroppedEntries:N0} 条；写入失败：{status.FailedEntries:N0} 条。"
            + $"\n队列载荷：{status.PendingBytes / 1048576d:F2} M；可用磁盘：{(status.AvailableDiskBytes.HasValue ? (status.AvailableDiskBytes.Value / 1048576d).ToString("F0") + " M" : "未探测")}；磁盘保护：{(status.DiskProtectionActive ? "已暂停" : "未触发")}；保护丢弃：{status.DiskRejectedEntries:N0}。"
            + (string.IsNullOrWhiteSpace(status.FileError) ? "" : "\n文件写入错误：" + status.FileError);
        _loggingFileState.ForeColor = string.IsNullOrWhiteSpace(status.FileError) ? UiTheme.Muted : UiTheme.Red;
    }
}
