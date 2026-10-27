using System.ComponentModel;

namespace EdgeGateway.WinForms;

/// <summary>Official AntdUI switch and caption, retaining Boolean field bindings.</summary>
[DefaultEvent(nameof(CheckedChanged))]
public partial class GatewaySwitch : Panel
{
    public GatewaySwitch()
    {
        InitializeComponent();
        SetStyle(ControlStyles.Selectable, true);
        switchControl.CheckedChanged += (_, _) => CheckedChanged?.Invoke(this, EventArgs.Empty);
        switchControl.KeyDown += (_, e) => OnKeyDown(e);
        captionLabel.Click += (_, _) =>
        {
            if (!Enabled) return;
            switchControl.Focus();
            if (AutoCheck) Checked = !Checked;
        };
    }

    [DefaultValue(false)]
    public bool Checked { get => switchControl.Checked; set => switchControl.Checked = value; }
    [DefaultValue(true)]
    public bool AutoCheck { get => switchControl.AutoCheck; set => switchControl.AutoCheck = value; }
    public event EventHandler? CheckedChanged;
    internal AntdUI.Switch SwitchControl => switchControl;
    [Browsable(true), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => base.Text; set => base.Text = value; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var leading = UiTheme.Scale(this, 60);
        var width = proposedSize.Width > leading ? proposedSize.Width - leading : int.MaxValue;
        var textSize = TextRenderer.MeasureText(Text ?? "", Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak);
        return new Size(Math.Max(MinimumSize.Width, leading + textSize.Width),
            Math.Max(MinimumSize.Height, Math.Max(UiTheme.Scale(this, 32), textSize.Height + UiTheme.Scale(this, 8))));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (switchControl is null) return;
        var leading = UiTheme.Scale(this, 60);
        var height = UiTheme.Scale(this, 30);
        switchControl.Bounds = new Rectangle(0, (Height - height) / 2, UiTheme.Scale(this, 52), height);
        captionLabel.Bounds = new Rectangle(leading, 0, Math.Max(0, Width - leading), Height);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Space or Keys.Enter && Enabled && AutoCheck)
        {
            Checked = !Checked;
            e.Handled = true;
        }
    }
    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, switchControl.Bounds);
    }
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (captionLabel is not null) captionLabel.Text = Text;
        if (switchControl is not null) switchControl.AccessibleName = string.IsNullOrEmpty(AccessibleName) ? Text : AccessibleName;
        if (AutoSize) Parent?.PerformLayout(this, nameof(Text));
    }
}
