using System.Drawing;

namespace EdgeGateway.WinForms;

/// <summary>Pure 96-DPI geometry. Always scale the original logical value, never a previously scaled result.</summary>
internal static class DpiLayoutMath
{
    internal static int Scale(int logical, int dpi) =>
        (int)Math.Round(logical * Math.Max(1, dpi) / 96d, MidpointRounding.AwayFromZero);

    internal static Size Scale(Size logical, int dpi) => new(Scale(logical.Width, dpi), Scale(logical.Height, dpi));

    internal static Rectangle Fit(Rectangle bounds, Rectangle workArea)
    {
        var width = Math.Clamp(bounds.Width, 1, Math.Max(1, workArea.Width));
        var height = Math.Clamp(bounds.Height, 1, Math.Max(1, workArea.Height));
        return new Rectangle(Math.Clamp(bounds.X, workArea.Left, workArea.Right - width),
            Math.Clamp(bounds.Y, workArea.Top, workArea.Bottom - height), width, height);
    }

    internal static Size MinimumWindow(Size logicalClient, Size nonClient, int dpi, Rectangle workArea)
    {
        var client = Scale(logicalClient, dpi);
        return new Size(Math.Min(Math.Max(1, workArea.Width), client.Width + Math.Max(0, nonClient.Width)),
            Math.Min(Math.Max(1, workArea.Height), client.Height + Math.Max(0, nonClient.Height)));
    }
}
