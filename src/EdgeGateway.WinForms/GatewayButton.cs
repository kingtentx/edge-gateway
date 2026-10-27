namespace EdgeGateway.WinForms;

/// <summary>Keeps AntdUI's own auto sizing consistent with WinForms layout constraints.</summary>
public class GatewayButton : AntdUI.Button
{
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
