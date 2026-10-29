namespace EdgeGateway.WinForms;

/// <summary>Official label rendering with the existing wrapping/layout contract.</summary>
public class GatewayLabel : AntdUI.Label
{
    private readonly Dictionary<int, Size> measurements = [];
    private string? measuredText;
    private Font? measuredFont;
    private Padding measuredPadding;
    public GatewayLabel() { TextAlign = ContentAlignment.TopLeft; TextMultiLine = true; }
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => base.Text ?? string.Empty; set => base.Text = value; }
    public override bool AutoSize
    {
        get => base.AutoSize;
        set
        {
            base.AutoSize = value;
            if (value) AutoSizeMode = Dock is DockStyle.Fill or DockStyle.Top or DockStyle.Bottom ? AntdUI.TAutoSize.Height : AntdUI.TAutoSize.Auto;
        }
    }
    protected override void OnDockChanged(EventArgs e)
    {
        base.OnDockChanged(e);
        if (AutoSize) AutoSizeMode = Dock is DockStyle.Fill or DockStyle.Top or DockStyle.Bottom ? AntdUI.TAutoSize.Height : AntdUI.TAutoSize.Auto;
    }
    public bool UseMnemonic { get; set; }
    public bool UseCompatibleTextRendering { get; set; }
    public int PreferredHeight => GetPreferredSize(new Size(Width, 0)).Height;
    public override Size PSize
    {
        get
        {
            var measured = MeasureLabel(AutoSizeMode == AntdUI.TAutoSize.Height ? Width : MaximumSize.Width);
            // AntdUI applies PSize again during OnResize. A centered, fill-docked
            // caption must retain the height assigned by its layout row, rather
            // than shrink back to the text height and sit above its editor.
            // GetPreferredSize still reports the measured text height to the
            // parent, so wrapped captions can grow and shrink with the window.
            if (Dock == DockStyle.Fill && TextAlign == ContentAlignment.MiddleLeft
                && AutoSizeMode == AntdUI.TAutoSize.Height)
                return new Size(measured.Width, Height);
            return measured;
        }
    }
    private Size MeasureLabel(int width)
    {
        if (measurements is null) return new Size(100, 24);
        if (measuredText != Text || !ReferenceEquals(measuredFont, Font) || measuredPadding != Padding)
        {
            measurements.Clear(); measuredText = Text; measuredFont = Font; measuredPadding = Padding;
        }
        if (MaximumSize.Width > 0) width = width > 1 ? Math.Min(width, MaximumSize.Width) : MaximumSize.Width;
        if (width <= 1) width = int.MaxValue;
        if (measurements.TryGetValue(width, out var cached)) return cached;
        var measured = TextRenderer.MeasureText(Text ?? "", Font,
            new Size(Math.Max(1, width - Padding.Horizontal), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var size = new Size(Math.Min(width, measured.Width + Padding.Horizontal), measured.Height + Padding.Vertical + 2);
        if (measurements.Count > 16) measurements.Clear();
        measurements[width] = size;
        return size;
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (!AutoSize) return base.GetPreferredSize(proposedSize);
        return MeasureLabel(proposedSize.Width);
    }
}
