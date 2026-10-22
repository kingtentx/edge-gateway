using EdgeGateway.Model;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace EdgeGateway.WinForms;

internal sealed class TrendChart : Control
{
    private IReadOnlyList<TelemetrySample> _samples = [];
    private string _title = "选择单个点位后查询，查看数值趋势";

    public TrendChart()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        ForeColor = UiTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 9F);
        ResizeRedraw = true;
    }

    public void SetSamples(IReadOnlyList<TelemetrySample> samples, string title)
    {
        _samples = samples.OrderBy(x => x.Timestamp).ToArray();
        _title = title;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var labelBrush = new SolidBrush(UiTheme.Muted);
        using var titleBrush = new SolidBrush(UiTheme.Ink);
        using var linePen = new Pen(UiTheme.Blue, 2.2F);
        using var gridPen = new Pen(UiTheme.Border, 1);
        graphics.DrawString(_title, Font, titleBrush, 18, 12);
        var values = _samples.Where(x => x.Quality == SampleQuality.Good && x.NumericValue.HasValue).ToArray();
        if (values.Length < 2)
        {
            graphics.DrawString(_samples.Count == 0 ? "暂无趋势数据" : "至少需要 2 个质量正常的数值采样点；布尔值不绘制趋势。", Font, labelBrush, 24, 90);
            return;
        }
        var plot = new RectangleF(76, 42, Math.Max(30, Width - 110), Math.Max(30, Height - 78));
        var minimum = values.Min(x => x.NumericValue!.Value);
        var maximum = values.Max(x => x.NumericValue!.Value);
        var padding = Math.Abs(maximum - minimum) < 1e-9 ? Math.Max(1, Math.Abs(maximum) * 0.05) : (maximum - minimum) * 0.08;
        minimum -= padding; maximum += padding;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || !double.IsFinite(maximum - minimum))
        {
            graphics.DrawString("数值范围超出图表显示范围，请缩小查询范围。", Font, labelBrush, 24, 90);
            return;
        }
        var from = _samples[0].Timestamp;
        var to = _samples[^1].Timestamp;
        var duration = Math.Max(1, (to - from).TotalMilliseconds);
        for (var i = 0; i <= 3; i++)
        {
            var y = plot.Top + plot.Height * i / 3;
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            graphics.DrawString((maximum - (maximum - minimum) * i / 3).ToString("G5", CultureInfo.InvariantCulture), Font, labelBrush, 8, y - 8);
        }
        graphics.DrawString(from.ToLocalTime().ToString("HH:mm:ss"), Font, labelBrush, plot.Left, plot.Bottom + 8);
        var end = to.ToLocalTime().ToString("HH:mm:ss");
        graphics.DrawString(end, Font, labelBrush, plot.Right - graphics.MeasureString(end, Font).Width, plot.Bottom + 8);
        // Break the line at bad/non-numeric samples so outages cannot look like healthy data.
        PointF? previous = null;
        foreach (var sample in _samples)
        {
            if (sample.Quality != SampleQuality.Good || sample.NumericValue is not double value) { previous = null; continue; }
            var point = new PointF(plot.Left + (float)((sample.Timestamp - from).TotalMilliseconds / duration) * plot.Width,
                plot.Bottom - (float)((value - minimum) / (maximum - minimum)) * plot.Height);
            if (previous.HasValue) graphics.DrawLine(linePen, previous.Value, point);
            previous = point;
        }
    }
}
