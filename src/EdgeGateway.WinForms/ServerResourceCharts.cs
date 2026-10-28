using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal sealed class ServerUsageTrend : AntdUI.Chart
{
    private readonly List<Reading> _readings = [];
    internal const int WindowSeconds = 60;
    internal IReadOnlyList<Reading> Readings => _readings;
    internal Color SeriesColor { get; set; } = UiTheme.Blue;
    internal ServerUsageTrend()
    {
        Height = 120; Margin = System.Windows.Forms.Padding.Empty; TabStop = false;
        ChartType = AntdUI.TChartType.Line; ShowLegend = false; EnableAnimation = false;
        XMin = -60; XMax = 0; YMin = 0; YMax = 100;
        XAxisLabelFormat = "0'秒'"; YAxisLabelFormat = "0'%'";
        AccessibleRole = AccessibleRole.Graphic;
    }
    internal void AddReading(DateTimeOffset timestamp, double? percent) { Append(timestamp, percent); UpdateSeries(); }
    private void Append(DateTimeOffset timestamp, double? percent)
    {
        if (percent is { } value && (!double.IsFinite(value) || value < 0 || value > 100)) percent = null;
        if (_readings.Count > 0 && timestamp < _readings[^1].Timestamp) _readings.Clear();
        if (_readings.Count > 0 && timestamp == _readings[^1].Timestamp) _readings.RemoveAt(_readings.Count - 1);
        _readings.Add(new(timestamp, percent));
        _readings.RemoveAll(reading => reading.Timestamp < timestamp.AddSeconds(-WindowSeconds));
        if (_readings.Count > 121) _readings.RemoveRange(0, _readings.Count - 121);
        AccessibleDescription = percent is { } current ? $"当前 {current:F1}%，最近 60 秒趋势，纵轴 0 至 100%。" : "当前读数不可用，趋势中的空缺表示未取得有效读数。";
    }
    internal void ReplaceReadings(IEnumerable<Reading> readings)
    {
        _readings.Clear(); foreach (var reading in readings) Append(reading.Timestamp, reading.Percent); UpdateSeries();
    }
    internal void ClearHistory() { _readings.Clear(); UpdateSeries(); }
    private void UpdateSeries()
    {
        Datasets.Clear(); Title = _readings.Any(x => x.Percent.HasValue) ? null : "等待有效采样";
        if (_readings.Count > 0)
        {
            var end = _readings[^1].Timestamp;
            AntdUI.ChartDataset? segment = null; DateTimeOffset? previous = null;
            foreach (var reading in _readings)
            {
                if (reading.Percent is not { } value) { segment = null; previous = null; continue; }
                if (segment is null || previous is { } at && (reading.Timestamp - at).TotalSeconds > 3)
                {
                    segment = new AntdUI.ChartDataset(AccessibleName) { BorderColor = SeriesColor, BorderWidth = 2 };
                    Datasets.Add(segment);
                }
                segment.DataPoints.Add(new AntdUI.ChartDataPoint(null, (reading.Timestamp - end).TotalSeconds, value)
                { Tooltip = $"{reading.Timestamp.ToLocalTime():HH:mm:ss} · {value:F1}%" });
                previous = reading.Timestamp;
            }
        }
        Invalidate();
    }
    internal sealed record Reading(DateTimeOffset Timestamp, double? Percent);
}

internal sealed partial class ServerDiskBar : AntdUI.Panel
{
    private ServerDiskUsage _disk;
    internal double? UsedPercent => _disk.Error is null ? _disk.UsedPercent : null;
    internal ServerDiskBar(ServerDiskUsage disk) { _disk = disk; InitializeComponent(); UpdateUsage(disk); }
    internal static string Capacity(long bytes) => $"{bytes / 1048576d:F0} M";
    internal void UpdateUsage(ServerDiskUsage disk)
    {
        _disk = disk; AccessibleName = $"磁盘 {disk.Name}";
        diskName.Text = disk.Name;
        diskValue.Text = UsedPercent is { } used ? $"{used:F1}%{(used >= 90 ? " · 空间偏紧" : " 已用")}" : "不可用";
        diskDetails.Text = UsedPercent is not null ? $"可用 {Capacity(disk.FreeBytes!.Value)} / 总量 {Capacity(disk.TotalBytes!.Value)}" : "不可用：" + disk.Error;
        diskProgress.Visible = UsedPercent.HasValue;
        diskProgress.Value = (float)(Math.Clamp(UsedPercent ?? 0, 0, 100) / 100);
        diskProgress.State = UsedPercent >= 90 ? AntdUI.TType.Warn : AntdUI.TType.None;
        AccessibleDescription = diskValue.Text + "，" + diskDetails.Text; MeasureRow();
    }
    private void MeasureRow()
    {
        if (diskDetails is null) return;
        var details = TextRenderer.MeasureText(diskDetails.Text, Font, new Size(Math.Max(1, ClientSize.Width - UiTheme.Scale(this, 24)), int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        var height = Font.Height + details.Height + UiTheme.Scale(this, 40);
        if (Height != height) Height = height;
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); MeasureRow(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); MeasureRow(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); MeasureRow(); }
}
