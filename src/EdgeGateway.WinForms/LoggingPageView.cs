namespace EdgeGateway.WinForms;

/// <summary>Fixed log page tabs and settings action area; archive query controls remain data-driven.</summary>
internal sealed partial class LoggingPageView : UserControl
{
    internal TabControl LogTabs => logTabs;
    internal TabPage EventLogsTab => eventLogsTab;
    internal TabPage SampleLogsTab => sampleLogsTab;
    internal Control SettingsFieldsHost => settingsFieldsHost;
    internal Label SettingsState => settingsState;
    internal AntdUI.Button ApplyButton => applyButton;
    internal AntdUI.Button ResetButton => resetButton;
    internal GatewayInput FileState => fileState;

    private void OnFooterSizeChanged(object? sender, EventArgs e)
    { settingsState.MaximumSize = new Size(Math.Max(1, settingsFooter.ClientSize.Width - settingsFooter.Padding.Horizontal), 0); }

    public LoggingPageView() => InitializeComponent();
}
