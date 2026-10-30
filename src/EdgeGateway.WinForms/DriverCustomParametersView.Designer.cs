namespace EdgeGateway.WinForms;

internal sealed partial class DriverCustomParametersView
{
    private GatewayTable grid = null!;
    private GatewayInput keyInput = null!, valueInput = null!;
    private GatewayButton addButton = null!, saveButton = null!, removeButton = null!;
    private Label hint = null!;

    private void InitializeComponent()
    {
        Dock = DockStyle.Fill; BackColor = Color.White; AutoScaleMode = AutoScaleMode.Inherit;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hint = new Label { Text = DefaultHint, AutoSize = true, Dock = DockStyle.Fill, ForeColor = UiTheme.Muted };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize)); fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var keyLabel = new Label { Text = "参数名", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true, Margin = new Padding(0, 3, 0, 5) };
        var valueLabel = new Label { Text = "参数值", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true, Margin = new Padding(0, 3, 0, 5) };
        keyInput = new GatewayInput { Name = "CustomParameterKey", Dock = DockStyle.Fill };
        valueInput = new GatewayInput { Name = "CustomParameterValue", Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        var keyFrame = new InputFrame { ContentWidth = 400, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 38, Margin = new Padding(0, 3, 0, 5) };
        var valueFrame = new InputFrame { ContentWidth = 400, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 38, Margin = new Padding(0, 3, 0, 5) };
        keyFrame.Controls.Add(keyInput); valueFrame.Controls.Add(valueInput);
        fields.Controls.Add(keyLabel, 0, 0); fields.Controls.Add(keyFrame, 1, 0);
        fields.Controls.Add(valueLabel, 0, 1); fields.Controls.Add(valueFrame, 1, 1);
        var actions = new WrappingToolbar { Dock = DockStyle.Top, AutoSize = true };
        addButton = new GatewayButton { Text = "新增参数", AutoSize = true, MinimumSize = new Size(100, 38), Margin = new Padding(0, 4, 8, 4) };
        saveButton = new GatewayButton { Text = "确认参数", AutoSize = true, MinimumSize = new Size(100, 38), Type = AntdUI.TTypeMini.Primary, Margin = new Padding(0, 4, 8, 4) };
        removeButton = new GatewayButton { Text = "删除参数", AutoSize = true, MinimumSize = new Size(100, 38), Margin = new Padding(0, 4, 8, 4) };
        actions.Controls.Add(addButton); actions.Controls.Add(saveButton); actions.Controls.Add(removeButton);
        grid = new GatewayTable { Name = "CustomDriverParameters", Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        var keyColumn = new GatewayTableColumn { Name = "Key", DataPropertyName = "Key", HeaderText = "参数名", MinimumWidth = 160 };
        var valueColumn = new GatewayTableColumn { Name = "Value", DataPropertyName = "Value", HeaderText = "参数值", MinimumWidth = 220 };
        grid.Columns.Add(keyColumn); grid.Columns.Add(valueColumn);
        layout.Controls.Add(hint, 0, 0); layout.Controls.Add(fields, 0, 1); layout.Controls.Add(actions, 0, 2); layout.Controls.Add(grid, 0, 3);
        Controls.Add(layout);
    }
}
