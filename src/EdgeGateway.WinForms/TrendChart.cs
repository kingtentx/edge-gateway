using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

/// <summary>Prepare discontinuous series; all chart rendering belongs to AntdUI.</summary>
internal sealed class TrendChart : AntdUI.Chart
{
    private IReadOnlyList<TelemetrySample> _samples = [];
    private string _title = "选择单个点位后查询，查看数值趋势";
    public TrendChart()
    {
        ChartType = AntdUI.TChartType.Line; ShowLegend = false; EnableAnimation = false;
        Title = _title; XAxisLabelFormat = "0.##'秒'";
    }
    public void SetSamples(IReadOnlyList<TelemetrySample> samples, string title)
    {
        _samples = samples.OrderBy(x => x.Timestamp).ToArray(); _title = title;
        Datasets.Clear(); Title = title;
        var values = _samples.Where(x => x.Quality == SampleQuality.Good && x.NumericValue is double v && double.IsFinite(v)).ToArray();
        if (values.Length < 2)
        {
            Title += _samples.Count == 0 ? " · 暂无趋势数据" : " · 至少需要两个质量正常的数值采样";
            Invalidate(); return;
        }
        var min = values.Min(x => x.NumericValue!.Value); var max = values.Max(x => x.NumericValue!.Value);
        var padding = Math.Abs(max - min) < 1e-9 ? Math.Max(1, Math.Abs(max) * .05) : (max - min) * .08;
        if (!double.IsFinite(min - padding) || !double.IsFinite(max + padding) || !double.IsFinite(max - min + 2 * padding))
        {
            Title += " · 数值范围超出图表显示范围，请缩小查询范围";
            Invalidate(); return;
        }
        var from = _samples[0].Timestamp;
        Title += $" · 起点 {from.ToLocalTime():MM-dd HH:mm:ss}（横轴为经过秒数）";
        XMin = 0; XMax = Math.Max(1, (_samples[^1].Timestamp - from).TotalSeconds);
        YMin = min - padding; YMax = max + padding;
        AntdUI.ChartDataset? segment = null;
        foreach (var sample in _samples)
        {
            if (sample.Quality != SampleQuality.Good || sample.NumericValue is not double value || !double.IsFinite(value))
            { segment = null; continue; }
            if (segment is null)
            {
                segment = new AntdUI.ChartDataset(title) { BorderColor = UiTheme.Blue, BorderWidth = 2 };
                Datasets.Add(segment);
            }
            segment.DataPoints.Add(new AntdUI.ChartDataPoint(null, (sample.Timestamp - from).TotalSeconds, value)
            { Tooltip = $"{sample.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} · {value:G17}" });
        }
        Invalidate();
    }
}
