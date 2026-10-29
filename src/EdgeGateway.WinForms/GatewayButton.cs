namespace EdgeGateway.WinForms;

/// <summary>Keeps AntdUI's own auto sizing consistent with WinForms layout constraints.</summary>
public class GatewayButton : AntdUI.Button
{
    // Use the official outline so Default actions remain distinct on white surfaces.
    // AntdUI continues to own all colors and interaction states.
    public GatewayButton() { WaveSize = 0; BorderWidth = 1; }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        // AntdUI auto sizing can run from SizeChanged while WinForms is still
        // scaling padding and minimum bounds. Measure once those values agree.
        var mode = AutoSizeMode;
        AutoSizeMode = AntdUI.TAutoSize.None;
        try { base.ScaleControl(factor, specified); }
        finally { AutoSizeMode = mode; }
    }

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
