#nullable enable
using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class DiagnosticsPageView
{
    private System.ComponentModel.IContainer? components;
    private TableLayoutPanel contentLayout = null!;
    private Label titleLabel = null!;
    private Label descriptionLabel = null!;
    private FlowLayoutPanel toolbarHost = null!;
    private GatewayTable diagnosticsGrid = null!;


    internal GatewayTableColumn diagnosticsGridColumnItem = null!;
    internal GatewayTableColumn diagnosticsGridColumnValue = null!;
    internal GatewayTableColumn diagnosticsGridColumnMeaning = null!;
    internal AntdUI.Button refreshButton = null!;
    internal AntdUI.Button exportButton = null!;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        contentLayout = new TableLayoutPanel();
        titleLabel = new Label();
        titleLabel.Name = "DiagnosticsPageTitle";
        titleLabel.Text = "通信诊断";
        titleLabel.AutoSize = true;
        titleLabel.Dock = DockStyle.Fill;
        titleLabel.Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold);
        titleLabel.ForeColor = UiTheme.Ink;
        titleLabel.Margin = new Padding(0, 0, 0, 2);
        descriptionLabel = new Label();
        descriptionLabel.Name = "DiagnosticsPageDescription";
        descriptionLabel.Text = "检查采集与上报链路。发送确认按平台协议判断，最终业务入库仍需在平台核对。";
        descriptionLabel.AutoSize = true;
        descriptionLabel.Dock = DockStyle.Fill;
        descriptionLabel.Font = new Font("Microsoft YaHei UI", 10F);
        descriptionLabel.ForeColor = UiTheme.Muted;
        descriptionLabel.Margin = new Padding(0, 0, 0, 8);
        toolbarHost = new WrappingToolbar();
        toolbarHost.Name = "DiagnosticsToolbarHost";
        toolbarHost.Dock = DockStyle.Fill;
        toolbarHost.AutoSize = true;
        toolbarHost.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        toolbarHost.WrapContents = true;
        toolbarHost.FlowDirection = FlowDirection.LeftToRight;
        toolbarHost.Margin = Padding.Empty;
        toolbarHost.Padding = new Padding(0, 4, 0, 4);
        diagnosticsGrid = new GatewayTable();
        diagnosticsGrid.Name = "diagnosticsGrid";
        diagnosticsGrid.Dock = DockStyle.Fill;
        diagnosticsGrid.BackgroundColor = Color.White;
        diagnosticsGrid.BorderStyle = BorderStyle.None;
        diagnosticsGrid.ReadOnly = true;
        diagnosticsGrid.AllowUserToAddRows = false;
        diagnosticsGrid.AllowUserToDeleteRows = false;
        diagnosticsGrid.AllowUserToResizeRows = false;
        diagnosticsGrid.AutoGenerateColumns = false;
        diagnosticsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        diagnosticsGrid.MultiSelect = false;
        diagnosticsGrid.RowHeadersVisible = false;
        diagnosticsGrid.EnableHeadersVisualStyles = false;
        diagnosticsGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        diagnosticsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        diagnosticsGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        diagnosticsGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        diagnosticsGrid.GridColor = UiTheme.Border;
        diagnosticsGrid.DefaultCellStyle.SelectionBackColor = UiTheme.BlueSoft;
diagnosticsGrid.DefaultCellStyle.SelectionForeColor = UiTheme.Ink;
diagnosticsGrid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.SurfaceSubtle;
        diagnosticsGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UiTheme.SurfaceSubtle;
        diagnosticsGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = UiTheme.Ink;
diagnosticsGrid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink;
        diagnosticsGrid.Name = "TelemetryDiagnostics";
        diagnosticsGrid.Dock = DockStyle.Fill;
        contentLayout.SuspendLayout();
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Inherit;
        Name = "DiagnosticsPageView";
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(16, 12, 16, 8);
        contentLayout.Name = "DiagnosticsContent";
        contentLayout.Dock = DockStyle.Fill;
        contentLayout.ColumnCount = 1;
        contentLayout.RowCount = 4;
        contentLayout.Margin = Padding.Empty;
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        contentLayout.Controls.Add(titleLabel, 0, 0);
        contentLayout.Controls.Add(descriptionLabel, 0, 1);
        contentLayout.Controls.Add(toolbarHost, 0, 2);
        contentLayout.Controls.Add(diagnosticsGrid, 0, 3);
        Controls.Add(contentLayout);
        contentLayout.ResumeLayout(false);
        contentLayout.PerformLayout();
        diagnosticsGridColumnItem = new GatewayTableColumn();
        diagnosticsGridColumnItem.Name = "diagnosticsGridColumnItem";
        diagnosticsGridColumnItem.DataPropertyName = "Item";
        diagnosticsGridColumnItem.HeaderText = "诊断项目";
        diagnosticsGridColumnItem.MinimumWidth = 180;
        diagnosticsGridColumnItem.FillWeight = 160;
        diagnosticsGridColumnItem.SortMode = DataGridViewColumnSortMode.NotSortable;
        diagnosticsGridColumnItem.Name = "Item";
        diagnosticsGrid.Columns.Add(diagnosticsGridColumnItem);
        diagnosticsGridColumnValue = new GatewayTableColumn();
        diagnosticsGridColumnValue.Name = "diagnosticsGridColumnValue";
        diagnosticsGridColumnValue.DataPropertyName = "Value";
        diagnosticsGridColumnValue.HeaderText = "当前值 / 最近事件";
        diagnosticsGridColumnValue.MinimumWidth = 220;
        diagnosticsGridColumnValue.FillWeight = 180;
        diagnosticsGridColumnValue.LineBreak = true;
        diagnosticsGridColumnValue.SortMode = DataGridViewColumnSortMode.NotSortable;
        diagnosticsGridColumnValue.Name = "Value";
        diagnosticsGrid.Columns.Add(diagnosticsGridColumnValue);
        diagnosticsGridColumnMeaning = new GatewayTableColumn();
        diagnosticsGridColumnMeaning.Name = "diagnosticsGridColumnMeaning";
        diagnosticsGridColumnMeaning.DataPropertyName = "Meaning";
        diagnosticsGridColumnMeaning.HeaderText = "说明";
        diagnosticsGridColumnMeaning.MinimumWidth = 350;
        diagnosticsGridColumnMeaning.FillWeight = 500;
        diagnosticsGridColumnMeaning.LineBreak = true;
        diagnosticsGridColumnMeaning.SortMode = DataGridViewColumnSortMode.NotSortable;
        diagnosticsGridColumnMeaning.Name = "Meaning";
        diagnosticsGrid.Columns.Add(diagnosticsGridColumnMeaning);
        refreshButton = new GatewayButton();
        refreshButton.Name = "refreshButton";
        refreshButton.Text = "刷新状态";
        refreshButton.AccessibleName = "刷新状态";
        refreshButton.AutoSize = true;
        refreshButton.MinimumSize = new Size(100, 38);
        refreshButton.Height = 38;
        refreshButton.Radius = 6;
        refreshButton.WaveSize = 0;
        refreshButton.Type = AntdUI.TTypeMini.Default;
        refreshButton.Padding = new Padding(10, 4, 10, 4);
        refreshButton.Margin = new Padding(0, 0, 8, 6);
        refreshButton.Cursor = Cursors.Hand;
        toolbarHost.Controls.Add(refreshButton);
        exportButton = new GatewayButton();
        exportButton.Name = "exportButton";
        exportButton.Text = "导出诊断";
        exportButton.AccessibleName = "导出诊断";
        exportButton.AutoSize = true;
        exportButton.MinimumSize = new Size(100, 38);
        exportButton.Height = 38;
        exportButton.Radius = 6;
        exportButton.WaveSize = 0;
        exportButton.Type = AntdUI.TTypeMini.Default;
        exportButton.Padding = new Padding(10, 4, 10, 4);
        exportButton.Margin = new Padding(0, 0, 8, 6);
        exportButton.Cursor = Cursors.Hand;
        toolbarHost.Controls.Add(exportButton);
        ResumeLayout(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }
}
