using System.ComponentModel;

namespace EdgeGateway.WinForms;

/// <summary>Caption-free AntdUI switch, retaining Boolean field bindings and accessible text.</summary>
[DefaultEvent(nameof(CheckedChanged))]
public partial class GatewaySwitch : Panel
{
    public GatewaySwitch()
    {
        InitializeComponent();
        SetStyle(ControlStyles.Selectable, true);
        switchControl.CheckedChanged += (_, _) => CheckedChanged?.Invoke(this, EventArgs.Empty);
        switchControl.KeyDown += (_, e) => OnKeyDown(e);
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
        return new Size(Math.Max(MinimumSize.Width, UiTheme.Scale(this, 52)),
            Math.Max(MinimumSize.Height, UiTheme.Scale(this, 32)));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (switchControl is null) return;
        var height = UiTheme.Scale(this, 30);
        switchControl.Bounds = new Rectangle(0, (Height - height) / 2, UiTheme.Scale(this, 52), height);
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
        if (switchControl is not null) switchControl.AccessibleName = string.IsNullOrEmpty(AccessibleName) ? Text : AccessibleName;
        if (AutoSize) Parent?.PerformLayout(this, nameof(Text));
    }
}
