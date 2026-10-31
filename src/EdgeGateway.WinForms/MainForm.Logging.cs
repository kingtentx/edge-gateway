using EdgeGateway.Model;
using System.Diagnostics;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private TabControl _logTabs = null!;
    private readonly Dictionary<string, Control> _loggingFields = [];
    private Label _loggingState = null!;
    private AntdUI.Button _loggingApply = null!, _loggingReset = null!;
    private LoggingPageView _loggingPageView = null!;
    private bool _loggingDirty, _bindingLogging;

    private Control WithLoggingSettings()
    {
        _loggingPageView = new LoggingPageView();
        _logTabs = _loggingPageView.LogTabs;
        _loggingState = _loggingPageView.SettingsState;
        _loggingApply = _loggingPageView.ApplyButton;
        _loggingReset = _loggingPageView.ResetButton;
        _loggingFields.Add("MinimumLevel", _loggingPageView.MinimumLevelInput);
        _loggingFields.Add("MaxFileSizeMb", _loggingPageView.MaxFileSizeMbInput);
        _loggingFields.Add("RetainedFileCount", _loggingPageView.RetainedFileCountInput);
        _loggingFields.Add("RetainedDays", _loggingPageView.RetainedDaysInput);
        _loggingFields.Add("Samples", _loggingPageView.SamplesInput);
        _loggingFields.Add("SampleMaxFileSizeMb", _loggingPageView.SampleMaxFileSizeMbInput);
        _loggingFields.Add("SampleRetainedFileCount", _loggingPageView.SampleRetainedFileCountInput);
        _loggingFields.Add("SampleRetainedDays", _loggingPageView.SampleRetainedDaysInput);
        _loggingFields.Add("MinimumFreeDiskSpaceMb", _loggingPageView.MinimumFreeDiskSpaceMbInput);
        _loggingFields.Add("DiskResumeMarginMb", _loggingPageView.DiskResumeMarginMbInput);
        void Changed() { if (_bindingLogging) return; _loggingDirty = true; UpdateCommands(); }
        foreach (var control in _loggingFields.Values)
        {
            _configurationCommands.Add(control);
            if (control is NumericUpDown number) number.ValueChanged += (_, _) => Changed();
            else if (control is CheckBox check) check.CheckedChanged += (_, _) => Changed();
            else if (control is ComboBox choice) choice.SelectedIndexChanged += (_, _) => Changed();
        }
        var level = _loggingPageView.MinimumLevelInput;
        level.Items.AddRange(Enum.GetValues<GatewayLogLevel>().Cast<object>().ToArray());
        level.Format += (_, e) => { if (e.ListItem is GatewayLogLevel value) e.Value = LogLevelText(value); };
        _loggingPageView.openLogsButton.Click += (_, _) =>
        {
            try
            {
                var directory = _application.GetLoggingStatus().DirectoryPath;
                if (directory is null) throw new InvalidOperationException("当前宿主未启用文件日志。");
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception exception) { ShowError(exception.Message); }
        };
        _loggingApply.Click += async (_, _) => await RunBusyAsync(ApplyLoggingSettingsAsync);
        _loggingReset.Click += (_, _) => { _loggingDirty = false; BindLoggingFields(); UpdateCommands(); };
        _configurationCommands.AddRange([_loggingApply, _loggingReset]);
        _loggingPageView.SizeChanged += (_, _) => SetWrappingWidth(_loggingState, Math.Max(1, _loggingPageView.ClientSize.Width - 32));
        _ = BuildLogArchivePage(false);
        _ = BuildLogArchivePage(true);
        return _loggingPageView;
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
    }
}
