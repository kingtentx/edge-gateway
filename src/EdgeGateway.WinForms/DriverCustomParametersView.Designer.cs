namespace EdgeGateway.WinForms;

internal sealed partial class DriverCustomParametersView
{
    private GatewayTable grid = null!;
    private GatewayInput search = null!;
    private GatewayButton addButton = null!, editButton = null!, removeButton = null!;
    private void InitializeComponent()
    {
        Dock = DockStyle.Fill; BackColor = Color.White; AutoScaleMode = AutoScaleMode.Inherit;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new WrappingToolbar { Dock = DockStyle.Top, AutoSize = true };
        addButton = new GatewayButton { Text = "添加参数", AutoSize = true, MinimumSize = new Size(100, 38), Type = AntdUI.TTypeMini.Primary, Margin = new Padding(0, 0, 8, 6) };
        editButton = new GatewayButton { Text = "编辑参数", AutoSize = true, MinimumSize = new Size(100, 38), Margin = new Padding(0, 0, 8, 6) };
        removeButton = new GatewayButton { Text = "删除参数", AutoSize = true, MinimumSize = new Size(100, 38), Margin = new Padding(0, 0, 8, 6) };
        search = new GatewayInput { Name = "DeviceParameterSearch", PlaceholderText = "搜索参数名称 / 标识", Dock = DockStyle.Fill };
        var searchFrame = new InputFrame { ContentWidth = 260, Width = 260, Height = 38, Margin = new Padding(0, 0, 0, 6) };
        searchFrame.Controls.Add(search);
        actions.Controls.Add(addButton); actions.Controls.Add(editButton); actions.Controls.Add(removeButton); actions.Controls.Add(searchFrame);
        grid = new GatewayTable { Name = "DeviceParameters", Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        grid.Columns.Add(new GatewayTableColumn { Name = "Name", DataPropertyName = "Name", HeaderText = "参数名称", MinimumWidth = 140 });
        grid.Columns.Add(new GatewayTableColumn { Name = "Key", DataPropertyName = "Key", HeaderText = "参数标识", MinimumWidth = 140 });
        grid.Columns.Add(new GatewayTableColumn { Name = "Value", DataPropertyName = "Value", HeaderText = "参数值", MinimumWidth = 160 });
        grid.Columns.Add(new GatewayTableColumn { Name = "Type", DataPropertyName = "Type", HeaderText = "类型", MinimumWidth = 90 });
        grid.Columns.Add(new GatewayTableColumn { Name = "Description", DataPropertyName = "Description", HeaderText = "说明", MinimumWidth = 200 });
        layout.Controls.Add(actions, 0, 0); layout.Controls.Add(grid, 0, 1); Controls.Add(layout);
    }
}
