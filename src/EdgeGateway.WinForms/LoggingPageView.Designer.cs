using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class LoggingPageView
{
    private System.ComponentModel.IContainer? components;
    private TabControl logTabs = null!;
    private TabPage settingsTab = null!;
    private TabPage eventLogsTab = null!;
    private TabPage sampleLogsTab = null!;
    private TableLayoutPanel settingsContent = null!;
    private Panel settingsFieldsHost = null!;
    private TableLayoutPanel settingsFooter = null!;
    private Label settingsState = null!;
    private AntdUI.Button applyButton = null!;
    private AntdUI.Button resetButton = null!;
    private TextBox fileState = null!;

    internal TabControl LogTabs => logTabs;
    internal TabPage EventLogsTab => eventLogsTab;
    internal TabPage SampleLogsTab => sampleLogsTab;
    internal Control SettingsFieldsHost => settingsFieldsHost;
    internal Label SettingsState => settingsState;
    internal AntdUI.Button ApplyButton => applyButton;
    internal AntdUI.Button ResetButton => resetButton;
    internal TextBox FileState => fileState;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        logTabs = new EditorLayout.EditorTabs { Name = "LoggingTabs", Dock = DockStyle.Fill, Multiline = true };
        settingsTab = new TabPage("日志设置") { Name = "LoggingSettingsTab", BackColor = Color.White };
        eventLogsTab = new TabPage("运行日志") { Name = "EventArchive", BackColor = Color.White };
        sampleLogsTab = new TabPage("采样数据") { Name = "SampleArchive", BackColor = Color.White };
        settingsContent = new TableLayoutPanel { Name = "LoggingSettingsContent", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        settingsFieldsHost = new Panel { Name = "LoggingFieldsViewport", Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), Margin = Padding.Empty };
        settingsFooter = new TableLayoutPanel { Name = "LoggingApplyBar", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 8, 8, 4), Margin = Padding.Empty };
        settingsState = UiTheme.Label("日志设置已保存。", 10, UiTheme.Muted);
        applyButton = CreateActionButton("应用日志设置", "ApplyLoggingSettings", true);
        resetButton = CreateActionButton("重置日志设置", "ResetLoggingSettings", false);
        fileState = new TextBox { Name = "LoggingFileStatus", Multiline = true, ReadOnly = true, AutoSize = false, Height = 48, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = UiTheme.Muted, AccessibleName = "日志文件状态，可滚动查看并复制完整内容" };

        settingsContent.SuspendLayout();
        settingsFooter.SuspendLayout();
        settingsTab.SuspendLayout();
        SuspendLayout();
        Name = "LoggingPageView";
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        settingsContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        settingsContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        settingsContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        settingsFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        settingsFooter.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        settingsFooter.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        settingsState.Name = "LoggingDraftState";
        settingsState.Dock = DockStyle.Fill;
        settingsFooter.Controls.Add(settingsState, 0, 0);
        var actions = new FlowLayoutPanel { Name = "LoggingApplyActions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = Padding.Empty };
        actions.Controls.Add(applyButton);
        actions.Controls.Add(resetButton);
        settingsFooter.Controls.Add(actions, 0, 1);
        settingsFooter.SizeChanged += (_, _) => { settingsState.MaximumSize = new Size(Math.Max(1, settingsFooter.ClientSize.Width - settingsFooter.Padding.Horizontal), 0); };
        settingsContent.Controls.Add(settingsFieldsHost, 0, 0);
        settingsContent.Controls.Add(settingsFooter, 0, 1);
        settingsTab.Controls.Add(settingsContent);
        logTabs.TabPages.Add(settingsTab);
        logTabs.TabPages.Add(eventLogsTab);
        logTabs.TabPages.Add(sampleLogsTab);
        Controls.Add(logTabs);
        settingsFooter.ResumeLayout(false);
        settingsFooter.PerformLayout();
        settingsContent.ResumeLayout(false);
        settingsTab.ResumeLayout(false);
        ResumeLayout(false);
    }

    private static AntdUI.Button CreateActionButton(string text, string name, bool primary) => new()
    {
        Name = name, Text = text, AutoSize = true, MinimumSize = new Size(130, 38), Height = 38,
        Padding = new Padding(14, 6, 14, 6), Radius = 6,
        BackColor = primary ? UiTheme.Blue : Color.White,
        ForeColor = primary ? Color.White : UiTheme.Ink,
        Margin = new Padding(0, 0, 8, 6), Cursor = Cursors.Hand
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }
}
