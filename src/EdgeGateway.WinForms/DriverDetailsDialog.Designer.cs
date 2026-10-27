#nullable enable
using System.Drawing;
using System.Windows.Forms;

namespace EdgeGateway.WinForms;

internal sealed partial class DriverDetailsDialog
{
    private GatewayInput detailsText = null!;
    private AntdUI.Button closeButton = null!;
    private FlowLayoutPanel actions = null!;
    private TableLayoutPanel layout = null!;

    private void InitializeComponent()
    {
        SuspendLayout();
        detailsText = new GatewayInput();
        detailsText.Name = "detailsText";
        detailsText.Dock = DockStyle.Fill;
        detailsText.Margin = new Padding(0, 3, 0, 5);
        detailsText.MinimumSize = new Size(150, 30);
        detailsText.BorderStyle = BorderStyle.FixedSingle;
        detailsText.Multiline = true;
        detailsText.ReadOnly = true;
        detailsText.ScrollBars = ScrollBars.Vertical;
        detailsText.WordWrap = true;
        detailsText.BackColor = Color.White;
        detailsText.Name = "DriverDetailsText";
        closeButton = new GatewayButton();
        closeButton.Name = "closeButton";
        closeButton.Text = "关闭";
        closeButton.AccessibleName = "关闭";
        closeButton.AutoSize = true;
        closeButton.MinimumSize = new Size(100, 38);
        closeButton.Height = 38;
        closeButton.Radius = 6;
        closeButton.WaveSize = 0;
        closeButton.Type = AntdUI.TTypeMini.Default;
        closeButton.Padding = new Padding(10, 4, 10, 4);
        closeButton.Margin = new Padding(0, 0, 8, 6);
        closeButton.Cursor = Cursors.Hand;
        actions = new FlowLayoutPanel();
        actions.Name = "actions";
        actions.Dock = DockStyle.Fill;
        actions.AutoSize = true;
        actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        actions.WrapContents = true;
        actions.Margin = Padding.Empty;
        actions.Padding = new Padding(0, 4, 0, 4);
        actions.Controls.Add(closeButton);
        layout = new TableLayoutPanel();
        layout.Name = "layout";
        layout.Dock = DockStyle.Fill;
        layout.AutoSize = false;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        layout.ColumnCount = 1;
        layout.RowCount = 0;
        layout.Margin = Padding.Empty;
        layout.Padding = new Padding(8);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowCount = 2;
        layout.Padding = new Padding(16);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(detailsText, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        this.Controls.Add(layout);
        CancelButton = closeButton;
        ResumeLayout(false);
    }
}
