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
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (string.IsNullOrEmpty(AccessibleName)) AccessibleName = Text;
    }
}
