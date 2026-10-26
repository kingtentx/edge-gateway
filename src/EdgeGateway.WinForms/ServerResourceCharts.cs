using System.Drawing.Drawing2D;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

/// <summary>Presentation-only history. Missing readings and sampling gaps break the line.</summary>
internal sealed class ServerUsageTrend : Control
{
    private readonly List<Reading> _readings = [];
    private readonly ToolTip _tip = new();
    private string? _tipText;
    internal const int WindowSeconds = 60;
    internal IReadOnlyList<Reading> Readings => _readings;
    internal Color SeriesColor { get; set; } = UiTheme.Blue;

    internal ServerUsageTrend()
    {
        DoubleBuffered = true; ResizeRedraw = true; TabStop = false;
        Height = 132; Margin = Padding.Empty; BackColor = Color.White;
        AccessibleRole = AccessibleRole.Graphic;
    }

    internal void AddReading(DateTimeOffset timestamp, double? percent)
    {
        if (percent is { } value && (!double.IsFinite(value) || value < 0 || value > 100)) percent = null;
        if (_readings.Count > 0 && timestamp < _readings[^1].Timestamp) _readings.Clear();
        if (_readings.Count > 0 && timestamp == _readings[^1].Timestamp) _readings.RemoveAt(_readings.Count - 1);
        _readings.Add(new(timestamp, percent));
        _readings.RemoveAll(reading => reading.Timestamp < timestamp.AddSeconds(-WindowSeconds));
        if (_readings.Count > 121) _readings.RemoveRange(0, _readings.Count - 121);
        AccessibleDescription = percent is { } current ? $"当前 {current:F1}%，最近 60 秒趋势，纵轴 0 至 100%。" : "当前读数不可用，趋势中的空缺表示未取得有效读数。";
        Invalidate();
    }

    internal void ClearHistory() { _readings.Clear(); Invalidate(); }

    private Rectangle PlotBounds()
    {
        var labelWidth = TextRenderer.MeasureText("100%", Font).Width;
        var lineHeight = Font.Height;
        return new Rectangle(labelWidth + UiTheme.Scale(this, 6), lineHeight / 2,
            Math.Max(0, Width - labelWidth - UiTheme.Scale(this, 12)), Math.Max(0, Height - lineHeight * 2 - UiTheme.Scale(this, 10)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var plot = PlotBounds();
        if (plot.Width < UiTheme.Scale(this, 40) || plot.Height < Font.Height * 2) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        using var grid = new Pen(UiTheme.Border, Math.Max(1, scale));
        using var line = new Pen(SeriesColor, 2 * scale) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(Color.FromArgb(24, SeriesColor));
        using var dot = new SolidBrush(SeriesColor);
        for (var i = 0; i <= 2; i++)
        {
            var y = plot.Top + plot.Height * i / 2;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(g, (100 - 50 * i) + "%", Font,
                new Rectangle(0, y - Font.Height / 2, plot.Left - UiTheme.Scale(this, 6), Font.Height), UiTheme.Muted,
                TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
        var axisY = plot.Bottom + UiTheme.Scale(this, 6);
        TextRenderer.DrawText(g, "60 秒前", Font, new Point(plot.Left, axisY), UiTheme.Muted, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "最近一次", Font, new Rectangle(plot.Left, axisY, plot.Width, Font.Height), UiTheme.Muted,
            TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (_readings.Count == 0 || _readings.All(reading => reading.Percent is null))
        {
            TextRenderer.DrawText(g, "等待有效采样", Font, plot, UiTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return;
        }
        var end = _readings[^1].Timestamp;
        PointF? previous = null;
        DateTimeOffset? previousTime = null;
        foreach (var reading in _readings)
        {
            if (reading.Percent is not { } percent) { previous = null; previousTime = null; continue; }
            var point = new PointF(plot.Right - (float)((end - reading.Timestamp).TotalSeconds / WindowSeconds * plot.Width),
                plot.Bottom - (float)(percent / 100 * plot.Height));
            if (previous is { } from && previousTime is { } at && (reading.Timestamp - at).TotalSeconds <= 3)
            {
                g.FillPolygon(fill, [new(from.X, plot.Bottom), from, point, new(point.X, plot.Bottom)]);
                g.DrawLine(line, from, point);
            }
            g.FillEllipse(dot, point.X - 2 * scale, point.Y - 2 * scale, 4 * scale, 4 * scale);
            previous = point; previousTime = reading.Timestamp;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = PlotBounds();
        string? text = null;
        if (_readings.Count > 0 && plot.Width > 0 && plot.Contains(e.Location))
        {
            var time = _readings[^1].Timestamp.AddSeconds(-WindowSeconds * (plot.Right - e.X) / (double)plot.Width);
            var closest = _readings.MinBy(reading => Math.Abs((reading.Timestamp - time).TotalSeconds))!;
            text = Math.Abs((closest.Timestamp - time).TotalSeconds) <= 2
                ? $"{closest.Timestamp.ToLocalTime():HH:mm:ss} · {(closest.Percent is { } value ? $"{value:F1}%" : "不可用")}" : "此时段无采样";
        }
        if (_tipText != text) { _tipText = text; _tip.SetToolTip(this, text); }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }
    internal sealed record Reading(DateTimeOffset Timestamp, double? Percent);
}

/// <summary>A disk's capacity usage, independent of disk I/O activity.</summary>
internal sealed class ServerDiskBar : Control
{
    private ServerDiskUsage _disk;
    internal double? UsedPercent => _disk.Error is null ? _disk.UsedPercent : null;

    internal ServerDiskBar(ServerDiskUsage disk)
    {
        _disk = disk; DoubleBuffered = true; ResizeRedraw = true; TabStop = false;
        BackColor = Color.White; AccessibleRole = AccessibleRole.Graphic;
        Margin = Padding.Empty;
    }

    internal void UpdateUsage(ServerDiskUsage disk)
    {
        _disk = disk;
        AccessibleName = $"磁盘 {disk.Name}";
        AccessibleDescription = UsedPercent is { } used
            ? $"已用 {used:F1}%，可用 {Capacity(disk.FreeBytes!.Value)}，总量 {Capacity(disk.TotalBytes!.Value)}{(used >= 90 ? "，空间偏紧" : "")}" : "不可用：" + disk.Error;
        Height = Font.Height * 3 + UiTheme.Scale(this, 30);
        Invalidate();
    }

    internal static string Capacity(long bytes) => $"{bytes / 1048576d:N0} M";

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var percent = UsedPercent;
        var color = percent >= 90 ? UiTheme.Warning : percent is null ? UiTheme.Muted : UiTheme.Blue;
        var value = percent is { } used ? $"{used:F1}%{(used >= 90 ? " · 空间偏紧" : " 已用")}" : "不可用";
        var valueWidth = TextRenderer.MeasureText(value, Font).Width;
        TextRenderer.DrawText(g, _disk.Name, Font, new Rectangle(0, 0, Math.Max(0, Width - valueWidth - UiTheme.Scale(this, 8)), Font.Height),
            UiTheme.Ink, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, value, Font, new Rectangle(0, 0, Width, Font.Height), color,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Right);
        var bar = new Rectangle(0, Font.Height + UiTheme.Scale(this, 7), Width, UiTheme.Scale(this, 10));
        using var background = new SolidBrush(UiTheme.Background);
        using var fill = new SolidBrush(color);
        g.FillRectangle(background, bar);
        if (percent is { } fraction)
            g.FillRectangle(fill, bar.X, bar.Y, (float)(bar.Width * Math.Clamp(fraction, 0, 100) / 100), bar.Height);
        var details = percent is not null
            ? $"可用 {Capacity(_disk.FreeBytes!.Value)} / 总量 {Capacity(_disk.TotalBytes!.Value)}"
            : "未取得有效容量，请查看下方提示。";
        TextRenderer.DrawText(g, details, Font, new Rectangle(0, bar.Bottom + UiTheme.Scale(this, 6), Width, Font.Height * 2),
            UiTheme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateUsage(_disk); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (_disk is not null) UpdateUsage(_disk); }
}
