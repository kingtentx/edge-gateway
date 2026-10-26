using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EdgeGateway.WinForms;

/// <summary>Creates the lightweight Edge Gateway notification-area icon at runtime.</summary>
internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(18, 32, 52));
            using var outline = new Pen(Color.FromArgb(64, 94, 132), 1.2f);
            graphics.FillRoundedRectangle(background, new RectangleF(1.5f, 1.5f, 29f, 29f), 7f);
            graphics.DrawRoundedRectangle(outline, new RectangleF(1.5f, 1.5f, 29f, 29f), 7f);

            using var link = new Pen(Color.FromArgb(75, 197, 232), 2.5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(link, 9, 11, 16, 16);
            graphics.DrawLine(link, 16, 16, 23, 10);
            graphics.DrawLine(link, 16, 16, 16, 23);
            using var node = new SolidBrush(Color.FromArgb(82, 207, 239));
            graphics.FillEllipse(node, 5.5f, 7.5f, 7f, 7f);
            graphics.FillEllipse(node, 19.5f, 6.5f, 7f, 7f);
            graphics.FillEllipse(node, 12.5f, 12.5f, 7f, 7f);
            graphics.FillEllipse(node, 12.5f, 20f, 7f, 7f);
            using var online = new SolidBrush(Color.FromArgb(41, 207, 151));
            using var whiteRing = new Pen(Color.FromArgb(18, 32, 52), 1.2f);
            graphics.FillEllipse(online, 22.2f, 21.2f, 7f, 7f);
            graphics.DrawEllipse(whiteRing, 22.2f, 21.2f, 7f, 7f);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rectangle, float radius)
    {
        using var path = RoundedPath(rectangle, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF rectangle, float radius)
    {
        using var path = RoundedPath(rectangle, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
