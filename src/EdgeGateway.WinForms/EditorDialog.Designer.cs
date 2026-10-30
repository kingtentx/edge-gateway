#nullable enable
using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

public partial class EditorDialog
{
    private TableLayoutPanel _layout = null!;
    private TableLayoutPanel _heading = null!;
    private FlowLayoutPanel _buttons = null!;
    private Label _description = null!;
    private AntdUI.Button _confirm = null!;
    private AntdUI.Button _cancel = null!;
    private EditorFeedback _feedback = null!;
    protected TabControl Tabs = null!;

    private void InitializeComponent()
    {
        _layout = new TableLayoutPanel();
        _layout.Name = "EditorLayout";
        _layout.Dock = DockStyle.Fill;
        _layout.ColumnCount = 1;
        _layout.RowCount = 4;
        _layout.Padding = new Padding(16);
        _layout.Margin = Padding.Empty;
        _heading = new TableLayoutPanel();
        _heading.Name = "EditorHeading";
        _heading.Dock = DockStyle.Fill;
        _heading.AutoSize = true;
        _heading.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _heading.ColumnCount = 1;
        _heading.RowCount = 1;
        _heading.Margin = Padding.Empty;
        _heading.Padding = new Padding(0, 0, 0, 6);
        _description = new Label();
        _description.Name = "_description";
        _description.Text = "";
        _description.AutoSize = true;
        _description.Dock = DockStyle.Fill;
        _description.ForeColor = UiTheme.Muted;
        _description.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
        _description.Margin = new Padding(0, 4, 0, 4);
        _description.Name = "EditorHeadingDescription";
        Tabs = new EditorLayout.EditorTabs();
        Tabs.Name = "EditorTabs";
        Tabs.Dock = DockStyle.Fill;

        Tabs.Margin = Padding.Empty;
        _feedback = new EditorFeedback();
        _feedback.Name = "EditorValidation";
        _confirm = new GatewayButton();
        _confirm.Name = "_confirm";
        _confirm.Text = "保存配置";
        _confirm.AccessibleName = "保存配置";
        _confirm.AutoSize = true;
        _confirm.MinimumSize = new Size(100, 38);
        _confirm.Height = 38;
        _confirm.Radius = 6;
        _confirm.WaveSize = 0;
        _confirm.Type = AntdUI.TTypeMini.Primary;
        _confirm.Padding = new Padding(10, 4, 10, 4);
        _confirm.Margin = new Padding(0, 0, 8, 6);
        _confirm.Cursor = Cursors.Hand;
        _confirm.Name = "EditorConfirm";
        _confirm.TabIndex = 0;
        _cancel = new GatewayButton();
        _cancel.Name = "_cancel";
        _cancel.Text = "取消";
        _cancel.AccessibleName = "取消";
        _cancel.AutoSize = true;
        _cancel.MinimumSize = new Size(100, 38);
        _cancel.Height = 38;
        _cancel.Radius = 6;
        _cancel.WaveSize = 0;
        _cancel.Type = AntdUI.TTypeMini.Default;
        _cancel.Padding = new Padding(10, 4, 10, 4);
        _cancel.Margin = new Padding(0, 0, 8, 6);
        _cancel.Cursor = Cursors.Hand;
        _cancel.Name = "EditorCancel";
        _cancel.TabIndex = 1;
        _cancel.DialogResult = DialogResult.Cancel;
        _buttons = new FlowLayoutPanel();
        _buttons.Name = "_buttons";
        _buttons.Dock = DockStyle.Fill;
        _buttons.AutoSize = true;
        _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _buttons.WrapContents = true;
        _buttons.Margin = Padding.Empty;
        _buttons.Padding = new Padding(0, 4, 0, 4);
        _buttons.Controls.Add(_cancel);
        _buttons.Controls.Add(_confirm);
        _buttons.Name = "EditorActions";
        _buttons.FlowDirection = FlowDirection.RightToLeft;
        _buttons.WrapContents = false;
        _buttons.BackColor = Color.White;
        _buttons.Padding = new Padding(12, 14, 4, 4);

        _layout.SuspendLayout();
        _heading.SuspendLayout();
        _buttons.SuspendLayout();
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _heading.Controls.Add(_description, 0, 0);
        _layout.Controls.Add(_heading, 0, 0);
        _layout.Controls.Add(Tabs, 0, 1);
        _layout.Controls.Add(_feedback, 0, 2);
        _layout.Controls.Add(_buttons, 0, 3);
        Controls.Add(_layout);
        _buttons.ResumeLayout(false);
        _heading.ResumeLayout(false);
        _heading.PerformLayout();
        _layout.ResumeLayout(false);
        _layout.PerformLayout();
    }
}
