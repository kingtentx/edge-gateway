namespace EdgeGateway.WinForms;

/// <summary>Native flow layout measured by available width, never by the parent's spare height.</summary>
internal sealed class WrappingToolbar : FlowLayoutPanel
{
    public override Size GetPreferredSize(Size proposedSize)
    {
        // AutoSize table rows can offer the entire remaining page height to a
        // nested flow panel. Only width should constrain a wrapping command row.
        var width = proposedSize.Width;
        if (width <= 1 && Parent is not null)
            width = Math.Max(1, Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal);
        return base.GetPreferredSize(new Size(width, 1));
    }
}
