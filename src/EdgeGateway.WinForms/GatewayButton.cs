namespace EdgeGateway.WinForms;

/// <summary>Keeps AntdUI's own auto sizing consistent with WinForms layout constraints.</summary>
public class GatewayButton : AntdUI.Button
{
    public GatewayButton() => ApplyInteractionColors();

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        ApplyInteractionColors();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        ApplyInteractionColors();
        base.OnMouseEnter(e);
    }

    private void ApplyInteractionColors()
    {
        if (Type == AntdUI.TTypeMini.Primary)
        {
            BackHover = Color.FromArgb(9, 88, 217);
            BackActive = Color.FromArgb(0, 62, 179);
            ForeHover = ForeActive = Color.White;
        }
        else if (Type == AntdUI.TTypeMini.Default)
        {
            BackHover = Color.FromArgb(214, 232, 255);
            BackActive = Color.FromArgb(186, 215, 255);
            ForeHover = Color.FromArgb(9, 88, 217);
            ForeActive = Color.FromArgb(0, 62, 179);
        }
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
