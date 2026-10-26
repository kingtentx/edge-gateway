using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class PlatformPageView
{
    private System.ComponentModel.IContainer? components;
    private TableLayoutPanel contentLayout = null!;
    private Label titleLabel = null!;
    private Label descriptionLabel = null!;
    private TabControl platformTabs = null!;
    private TabPage connectionTab = null!;
    private TabPage reportingTab = null!;
    private TabPage rpcTab = null!;
    private TabPage gatewayTab = null!;
    private Panel connectionViewport = null!;
    private Panel reportingViewport = null!;
    private Panel rpcViewport = null!;
    private Panel gatewayViewport = null!;
    private TableLayoutPanel connectionFields = null!;
    private TableLayoutPanel reportingFields = null!;
    private TableLayoutPanel rpcFields = null!;
    private TableLayoutPanel gatewayFields = null!;
    private TableLayoutPanel applyBar = null!;
    private Label draftState = null!;
    private AntdUI.Button applyButton = null!;
    private AntdUI.Button resetButton = null!;

    internal TabControl PlatformTabs => platformTabs;
    internal TableLayoutPanel ConnectionFields => connectionFields;
    internal TableLayoutPanel ReportingFields => reportingFields;
    internal TableLayoutPanel RpcFields => rpcFields;
    internal TableLayoutPanel GatewayFields => gatewayFields;
    internal Label DraftState => draftState;
    internal AntdUI.Button ApplyButton => applyButton;
    internal AntdUI.Button ResetButton => resetButton;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        contentLayout = new TableLayoutPanel();
        titleLabel = new Label { Name = "PlatformPageTitle", Text = "平台与系统", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 21F, FontStyle.Bold), ForeColor = UiTheme.Ink, Margin = new Padding(0, 0, 0, 2) };
        descriptionLabel = new Label { Name = "PlatformPageDescription", Text = "分组维护连接、上报、远程控制和网关设置；编辑完成后统一应用。", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F), ForeColor = UiTheme.Muted, Margin = new Padding(0, 0, 0, 8) };
        platformTabs = new EditorLayout.EditorTabs { Name = "PlatformTabs", Dock = DockStyle.Fill, Multiline = true, Margin = Padding.Empty };
        connectionTab = new TabPage("平台连接") { Name = "PlatformTab_connection", BackColor = Color.White };
        reportingTab = new TabPage("上报策略") { Name = "PlatformTab_reporting", BackColor = Color.White };
        rpcTab = new TabPage("远程控制") { Name = "PlatformTab_rpc", BackColor = Color.White };
        gatewayTab = new TabPage("网关设置") { Name = "PlatformTab_gateway", BackColor = Color.White };
        connectionViewport = CreateViewport("PlatformFields_connection");
        reportingViewport = CreateViewport("PlatformFields_reporting");
        rpcViewport = CreateViewport("PlatformFields_rpc");
        gatewayViewport = CreateViewport("PlatformFields_gateway");
        connectionFields = EditorLayout.Fields(); connectionFields.Name = "PlatformTable_connection";
        reportingFields = EditorLayout.Fields(); reportingFields.Name = "PlatformTable_reporting";
        rpcFields = EditorLayout.Fields(); rpcFields.Name = "PlatformTable_rpc";
        gatewayFields = EditorLayout.Fields(); gatewayFields.Name = "PlatformTable_gateway";
        applyBar = new TableLayoutPanel { Name = "PlatformApplyBar", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(14, 8, 8, 4), Margin = Padding.Empty };
        draftState = UiTheme.Label("设置已应用", 10, UiTheme.Muted);
        applyButton = CreateActionButton("应用设置", "ApplyCloudSettings", true);
        resetButton = CreateActionButton("重置修改", "ResetCloudSettings", false);

        contentLayout.SuspendLayout();
        applyBar.SuspendLayout();
        connectionTab.SuspendLayout(); reportingTab.SuspendLayout(); rpcTab.SuspendLayout(); gatewayTab.SuspendLayout();
        SuspendLayout();
        Name = "PlatformPageView";
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(20);
        contentLayout.Name = "PlatformContent";
        contentLayout.Dock = DockStyle.Fill;
        contentLayout.ColumnCount = 1;
        contentLayout.RowCount = 4;
        contentLayout.Margin = Padding.Empty;
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ConfigureViewport(connectionViewport, connectionFields);
        ConfigureViewport(reportingViewport, reportingFields);
        ConfigureViewport(rpcViewport, rpcFields);
        ConfigureViewport(gatewayViewport, gatewayFields);
        connectionTab.Controls.Add(connectionViewport);
        reportingTab.Controls.Add(reportingViewport);
        rpcTab.Controls.Add(rpcViewport);
        gatewayTab.Controls.Add(gatewayViewport);
        platformTabs.TabPages.Add(connectionTab);
        platformTabs.TabPages.Add(reportingTab);
        platformTabs.TabPages.Add(rpcTab);
        platformTabs.TabPages.Add(gatewayTab);
        applyBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        applyBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        applyBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        draftState.Name = "PlatformDraftState";
        draftState.Dock = DockStyle.Fill;
        applyBar.Controls.Add(draftState, 0, 0);
        var actionPanel = new FlowLayoutPanel { Name = "PlatformApplyActions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = Padding.Empty };
        actionPanel.Controls.Add(applyButton);
        actionPanel.Controls.Add(resetButton);
        applyBar.Controls.Add(actionPanel, 0, 1);
        applyBar.SizeChanged += (_, _) => { if (draftState.Parent is not null) draftState.MaximumSize = new Size(Math.Max(1, applyBar.ClientSize.Width - applyBar.Padding.Horizontal), 0); };
        contentLayout.Controls.Add(titleLabel, 0, 0);
        contentLayout.Controls.Add(descriptionLabel, 0, 1);
        contentLayout.Controls.Add(platformTabs, 0, 2);
        contentLayout.Controls.Add(applyBar, 0, 3);
        Controls.Add(contentLayout);
        connectionTab.ResumeLayout(false); reportingTab.ResumeLayout(false); rpcTab.ResumeLayout(false); gatewayTab.ResumeLayout(false);
        applyBar.ResumeLayout(false); applyBar.PerformLayout();
        contentLayout.ResumeLayout(false); contentLayout.PerformLayout();
        ResumeLayout(false);
    }

    private static Panel CreateViewport(string name) => new()
    {
        Name = name, Dock = DockStyle.Fill, AutoScroll = true,
        BackColor = Color.White, Padding = new Padding(12), Margin = Padding.Empty
    };

    private static void ConfigureViewport(Panel viewport, TableLayoutPanel fields)
    {
        viewport.Controls.Add(fields);
    }

    private static AntdUI.Button CreateActionButton(string text, string name, bool primary) => new()
    {
        Name = name, Text = text, AutoSize = true, MinimumSize = new Size(110, 38),
        Height = 38, Padding = new Padding(14, 6, 14, 6), Radius = 6,
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
