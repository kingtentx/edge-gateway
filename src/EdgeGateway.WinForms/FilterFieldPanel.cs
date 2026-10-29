namespace EdgeGateway.WinForms;

/// <summary>Preserves declared editor widths when a filter row changes DPI.</summary>
internal sealed class FilterFieldPanel : TableLayoutPanel
{
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        // TableLayoutPanel scales its last absolute column from the last measured
        // cell width. During AutoSize/wrapping that cell may be temporarily squeezed.
        // Scale the declared width instead, without changing native layout/painting.
        var widths = ColumnStyles.Cast<ColumnStyle>()
            .Where(style => style.SizeType == SizeType.Absolute)
            .Select(style => (Style: style, Width: style.Width)).ToArray();
        SuspendLayout();
        try
        {
            base.ScaleControl(factor, specified);
            foreach (var (style, width) in widths)
                // Keep fractional logical widths through 125/150% transitions;
                // rounding each step loses pixels on repeated round trips.
                style.Width = width * factor.Width;
        }
        finally { ResumeLayout(true); }
    }
}
