namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private static void SetWrappingWidth(Label label, int availableWidth)
    {
        var maximum = new Size(Math.Max(1, availableWidth - label.Margin.Horizontal), 0);
        if (label.MaximumSize != maximum) label.MaximumSize = maximum;
    }

}
