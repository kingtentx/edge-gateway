using System.Drawing.Drawing2D;

namespace EdgeGateway.WinForms;

/// <summary>Switch appearance with the standard grid Boolean commit/keyboard behavior.</summary>
public sealed class GatewayGridSwitchColumn : DataGridViewCheckBoxColumn
{
    public GatewayGridSwitchColumn() => CellTemplate = new GatewayGridSwitchCell();
}

public sealed class GatewayGridSwitchCell : DataGridViewCheckBoxCell
{
    protected override Rectangle GetContentBounds(Graphics graphics, DataGridViewCellStyle cellStyle, int rowIndex)
    {
        var dpi = DataGridView?.DeviceDpi ?? 96;
        var width = 36 * dpi / 96;
        var height = 20 * dpi / 96;
        return new Rectangle((Size.Width - width) / 2, (Size.Height - height) / 2, width, height);
    }

    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
        DataGridViewElementStates cellState, object? value, object? formattedValue, string? errorText,
        DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
    {
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText,
            cellStyle, advancedBorderStyle, paintParts & ~DataGridViewPaintParts.ContentForeground);
        if (!paintParts.HasFlag(DataGridViewPaintParts.ContentForeground)) return;
        var bounds = GetContentBounds(graphics, cellStyle, rowIndex);
        bounds.Offset(cellBounds.Location);
        var enabled = DataGridView?.Enabled == true && !ReadOnly;
        var on = formattedValue is true || formattedValue is CheckState.Checked;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(Rectangle.Intersect(clipBounds, cellBounds));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, bounds.Height, bounds.Height, 90, 180);
            path.AddArc(bounds.Right - bounds.Height, bounds.Top, bounds.Height, bounds.Height, 270, 180);
            path.CloseFigure();
            using var track = new SolidBrush(on ? (enabled ? UiTheme.Blue : Color.FromArgb(145, 190, 255)) : Color.FromArgb(195, 199, 205));
            graphics.FillPath(track, path);
            var gap = Math.Max(2, 2 * (DataGridView?.DeviceDpi ?? 96) / 96);
            var diameter = bounds.Height - gap * 2;
            using var thumb = new SolidBrush(Color.White);
            graphics.FillEllipse(thumb, on ? bounds.Right - gap - diameter : bounds.Left + gap, bounds.Top + gap, diameter, diameter);
        }
        finally { graphics.Restore(state); }
    }
}
