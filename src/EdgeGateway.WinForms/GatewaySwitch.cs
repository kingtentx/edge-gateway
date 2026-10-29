using System.ComponentModel;

namespace EdgeGateway.WinForms;

/// <summary>Official Switch owns painting, focus and keyboard; the adapter keeps field events.</summary>
[DefaultEvent(nameof(CheckedChanged))]
public partial class GatewaySwitch : AntdUI.Switch
{
    public GatewaySwitch()
    {
        InitializeComponent();
        base.CheckedChanged += (_, _) => CheckedChanged?.Invoke(this, EventArgs.Empty);
    }
    public new event EventHandler? CheckedChanged;
    internal AntdUI.Switch SwitchControl => this;
    public override Size GetPreferredSize(Size proposedSize) => new(
        Math.Max(MinimumSize.Width, UiTheme.Scale(this, 52)),
        Math.Max(MinimumSize.Height, UiTheme.Scale(this, 30)));
    protected override void OnLayout(LayoutEventArgs e)
    {
        // DpiAwareForm lays out its tree after initial and per-monitor scaling.
        // HandleCreated runs before that transaction completes; resetting to logical
        // units there would undo the framework's already scaled maximum width.
        if (FindForm() is { } owner && owner is not DpiAwareForm { IsDpiLayoutReady: false })
        {
            var maximum = new Size(UiTheme.Scale(this, 52), 0);
            if (MaximumSize != maximum) MaximumSize = maximum;
        }
        base.OnLayout(e);
    }
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (string.IsNullOrEmpty(AccessibleName)) AccessibleName = Text;
    }
}
