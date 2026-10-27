#nullable enable
using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class EditorFeedback
{
    private Label _heading = null!;
    private LinkLabel _locate = null!;
    private GatewayInput _message = null!;

    private void InitializeComponent()
    {
        SuspendLayout();
        _heading = new Label();
        _heading.Name = "_heading";
        _heading.Text = "请检查后重试，输入内容已保留";
        _heading.AutoSize = true;
        _heading.Dock = DockStyle.Fill;
        _heading.ForeColor = UiTheme.Red;
        _heading.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _heading.Margin = new Padding(0, 4, 0, 4);
        _locate = new LinkLabel();
        _locate.Name = "EditorValidationLocate";
        _locate.Text = "定位到字段";
        _locate.AutoSize = true;
        _locate.Anchor = AnchorStyles.Right;
        _locate.Margin = new Padding(12, 0, 0, 0);
        _locate.LinkColor = UiTheme.Red;
        _locate.LinkClicked += OnLocateClicked;
        _message = new GatewayInput();
        _message.Name = "EditorValidationMessage";
        _message.Dock = DockStyle.Fill;
        _message.Margin = new Padding(0, 5, 0, 10);
        _message.MinimumSize = new Size(150, 30);
        _message.BorderStyle = BorderStyle.FixedSingle;
        _message.Multiline = true;
        _message.ReadOnly = true;
        _message.Height = 64;
        _message.ScrollBars = ScrollBars.Vertical;
        _message.BackColor = Color.FromArgb(255, 245, 245);
        _message.AccessibleName = "校验或保存错误详情";
        this.Visible = false;
        this.Dock = DockStyle.Fill;
        this.AutoSize = true;
        this.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        this.ColumnCount = 2;
        this.RowCount = 2;
        this.Padding = new Padding(12, 8, 12, 8);
        this.Margin = new Padding(0, 8, 0, 0);
        this.BackColor = Color.FromArgb(255, 245, 245);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        this.Controls.Add(_heading, 0, 0);
        this.Controls.Add(_locate, 1, 0);
        this.Controls.Add(_message, 0, 1);
        SetColumnSpan(_message, 2);
        ResumeLayout(false);
    }
}
