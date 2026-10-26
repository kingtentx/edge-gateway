using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal abstract partial class EditorDialog
{
    private TableLayoutPanel _layout = null!;
    private TableLayoutPanel _heading = null!;
    private TableLayoutPanel _titleRow = null!;
    private FlowLayoutPanel _buttons = null!;
    private Label _title = null!;
    private Label _description = null!;
    private LinkLabel _help = null!;
    private Button _confirm = null!;
    private Button _cancel = null!;
    private EditorFeedback _feedback = null!;
    protected TabControl Tabs = null!;

    private void InitializeComponent()
    {
        _layout = new TableLayoutPanel { Name = "EditorLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20), Margin = Padding.Empty };
        _heading = new TableLayoutPanel { Name = "EditorHeading", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 14) };
        _titleRow = new TableLayoutPanel { Name = "EditorTitleRow", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        _title = EditorLayout.WrappingLabel("", 20, UiTheme.Ink, true);
        _title.Name = "EditorHeadingTitle";
        _description = EditorLayout.WrappingLabel("", 10, UiTheme.Muted);
        _description.Name = "EditorHeadingDescription";
        _help = new LinkLabel { Name = "EditorHelp", Text = "查看说明", AutoSize = true, Visible = false, TabStop = true, LinkColor = UiTheme.Blue, ActiveLinkColor = UiTheme.Blue, LinkBehavior = LinkBehavior.HoverUnderline, Anchor = AnchorStyles.Right, Margin = new Padding(12, 8, 0, 0) };
        Tabs = new EditorLayout.EditorTabs { Name = "EditorTabs", Dock = DockStyle.Fill, Multiline = true, Margin = Padding.Empty };
        _feedback = new EditorFeedback("EditorValidation");
        _confirm = UiTheme.Button("保存配置", primary: true);
        _confirm.Name = "EditorConfirm";
        _cancel = UiTheme.Button("取消");
        _cancel.Name = "EditorCancel";
        _cancel.DialogResult = DialogResult.Cancel;
        _buttons = UiTheme.Toolbar(_confirm, _cancel);
        _buttons.Name = "EditorActions";
        _buttons.FlowDirection = FlowDirection.RightToLeft;
        _buttons.WrapContents = false;
        _buttons.BackColor = Color.White;
        _buttons.Padding = new Padding(12, 14, 4, 4);

        _layout.SuspendLayout();
        _heading.SuspendLayout();
        _titleRow.SuspendLayout();
        _buttons.SuspendLayout();
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _titleRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _titleRow.Controls.Add(_title, 0, 0);
        _titleRow.Controls.Add(_help, 1, 0);
        _heading.Controls.Add(_titleRow, 0, 0);
        _heading.Controls.Add(_description, 0, 1);
        _buttons.Paint += (_, e) => { using var pen = new Pen(UiTheme.Border); e.Graphics.DrawLine(pen, 0, 0, _buttons.Width, 0); };
        _layout.Controls.Add(_heading, 0, 0);
        _layout.Controls.Add(Tabs, 0, 1);
        _layout.Controls.Add(_feedback, 0, 2);
        _layout.Controls.Add(_buttons, 0, 3);
        Controls.Add(_layout);
        _buttons.ResumeLayout(false);
        _heading.ResumeLayout(false);
        _heading.PerformLayout();
        _titleRow.ResumeLayout(false);
        _titleRow.PerformLayout();
        _layout.ResumeLayout(false);
        _layout.PerformLayout();
    }
}
