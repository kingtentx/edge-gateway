namespace EdgeGateway.WinForms;

/// <summary>Keeps AntdUI's own auto sizing consistent with WinForms layout constraints.</summary>
public class GatewayButton : AntdUI.Button
{
    // AntdUI-Demo's basic Default / Primary examples disable the wave and leave
    // every color, border and interaction state to AntdUI's defaults.
    public GatewayButton() => WaveSize = 0;

    public override Size PSize
    {
        get
        {
            var preferred = base.PSize;
            return new Size(Math.Max(MinimumSize.Width, preferred.Width + Padding.Horizontal),
                Math.Max(MinimumSize.Height, preferred.Height + Padding.Vertical));
        }
    }
}
