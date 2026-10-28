using EdgeGateway.Model;
using System.Globalization;
using System.Text;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private DataGridView _realtimeGrid = null!;
    private DataGridView _historyGrid = null!;
    private ComboBox _realtimeDevice = null!;
    private TextBox _realtimeSearch = null!;
    private ComboBox _historySource = null!;
    private NumericUpDown _historyScanLimit = null!;
    private bool _executedHistoryFromLogs;
    private int _executedHistoryScanLimit;
    private bool HistoryFromLogs => _historySource.SelectedIndex == 0;
    private ComboBox _historyGroup = null!;
    private ComboBox _historyDevice = null!;
    private ComboBox _historyTag = null!;
    private DateTimePicker _historyFrom = null!;
    private DateTimePicker _historyTo = null!;
    private NumericUpDown _historyLimit = null!;
    private TrendChart _trend = null!;
    private Label _historySummary = null!;
    private IReadOnlyList<TelemetrySample> _queriedHistory = [];
    private Button _writeButton = null!, _historyExport = null!, _historyQuery = null!;
    private HistoryQuery? _executedHistoryQuery;
    private Guid? _executedHistoryGroupId;
    private string _executedHistorySummary = "";
    private bool _historyConditionsChanged;
    private RealtimePageView _realtimePageView = null!;
    private HistoryPageView _historyPageView = null!;

    private Control BuildRealtimePage()
    {
        _realtimePageView = new RealtimePageView();
        _realtimeGrid = _realtimePageView.RealtimeGrid;
        _realtimeDevice = _realtimePageView.DeviceSelector;
        _realtimeSearch = _realtimePageView.PointSearch;
        _realtimeGrid.CellFormatting += SampleCellFormatting;
        _realtimeGrid.SelectionChanged += (_, _) => UpdateWriteButton();
        _realtimeDevice.SelectedIndexChanged += (_, _) => RefreshRealtime(_application.GetLatest());
        _realtimeSearch.TextChanged += (_, _) => RefreshRealtime(_application.GetLatest());
        _writeButton = _realtimePageView.writeButton;
        _writeButton.Click += async (_, _) => await RunBusyAsync(WritePointAsync);
        return _realtimePageView;
    }

    private Control BuildHistoryPage()
    {
        _historyPageView = new HistoryPageView();
        _historyGrid = _historyPageView.HistoryGrid;
        _historySource = _historyPageView.SourceSelector;
        _historyScanLimit = _historyPageView.ScanLimit;
        _historyGroup = _historyPageView.GroupSelector;
        _historyDevice = _historyPageView.DeviceSelector;
        _historyTag = _historyPageView.PointSelector;
        _historyFrom = _historyPageView.FromTime;
        _historyTo = _historyPageView.ToTime;
        _historyLimit = _historyPageView.ResultLimit;
        _historySummary = _historyPageView.QuerySummary;
        _trend = _historyPageView.TrendChart;
        _historyGrid.CellFormatting += SampleCellFormatting;
        _historyDevice.SelectedIndexChanged += (_, _) => BindHistoryTags();
        _historyFrom.Value = DateTime.Now.AddHours(-1); _historyTo.Value = DateTime.Now.AddMinutes(1);
        _historyQuery = _historyPageView.queryButton; _historyQuery.Name = "QueryExternalHistory";
        _historyQuery.Click += async (_, _) => await RunBusyAsync(QueryHistoryAsync);
        _historyGroup.SelectedIndexChanged += (_, _) => UpdateHistoryQueryState();
        _historyExport = _historyPageView.exportButton;
        _historyExport.Click += async (_, _) => await RunBusyAsync(ExportHistoryAsync);
        _historyPageView.recentButton.Click += (_, _) => { _historyTo.Value = DateTime.Now; _historyFrom.Value = DateTime.Now.AddHours(-1); };
        _historyDevice.SelectedIndexChanged += (_, _) => UpdateHistoryQueryState();
        _historyTag.SelectedIndexChanged += (_, _) => UpdateHistoryQueryState();
        _historyFrom.ValueChanged += (_, _) => UpdateHistoryQueryState();
        _historyTo.ValueChanged += (_, _) => UpdateHistoryQueryState();
        _historyLimit.ValueChanged += (_, _) => UpdateHistoryQueryState();
        _historyScanLimit.ValueChanged += (_, _) => UpdateHistoryQueryState();
        _historySource.SelectedIndexChanged += (_, _) =>
        {
            _historyLimit.Maximum = HistoryFromLogs ? 2000 : 10000;
            if (_historyLimit.Value > _historyLimit.Maximum) _historyLimit.Value = _historyLimit.Maximum;
            UpdateHistoryQueryState();
        };
        _trend.Visible = false;
        void ArrangeHistoryLayout()
        {
            if (!IsDpiLayoutReady) return;
            var layout = _historyPageView.ContentLayout;
            var height = _trend.Visible ? UiTheme.Scale(layout, 220) : 0;
            if (layout.RowStyles[4].Height != height) layout.RowStyles[4].Height = height;
            SetWrappingWidth(_historySummary, Math.Max(1, layout.ClientSize.Width));
        }
        _historyPageView.ContentLayout.SizeChanged += (_, _) => ArrangeHistoryLayout();
        _trend.VisibleChanged += (_, _) => ArrangeHistoryLayout();
        DpiLayoutCompleted += (_, _) => ArrangeHistoryLayout();
        return _historyPageView;
    }

    private Control BuildLogsPage() => WithLoggingSettings();

    private void BindDeviceChoices()
    {
        var realtimeSelected = (_realtimeDevice.SelectedItem as Choice)?.Id;
        var historySelected = (_historyDevice.SelectedItem as Choice)?.Id;
        var choices = new[] { new Choice(null, "全部设备") }.Concat(_configuration.Devices.Select(x => new Choice(x.Id, x.Name))).ToArray();
        _realtimeDevice.DataSource = choices.ToArray(); _historyDevice.DataSource = choices.ToArray();
        SelectChoice(_realtimeDevice, realtimeSelected); SelectChoice(_historyDevice, historySelected); BindHistoryTags();
    }

    private static void SelectChoice(ComboBox box, Guid? id)
    {
        for (var i = 0; i < box.Items.Count; i++) if (box.Items[i] is Choice choice && choice.Id == id) { box.SelectedIndex = i; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void BindHistoryGroups()
    {
        var selected = (_historyGroup.SelectedItem as Choice)?.Id;
        var choices = _configuration.DatabaseWriteGroups.Where(group => group.Enabled && group.Mode == DatabaseStorageMode.PointRows
            && _configuration.Databases.Any(connection => connection.Id == group.ConnectionId && connection.Enabled))
            .Select(group => new Choice(group.Id, _configuration.Databases.Single(connection => connection.Id == group.ConnectionId).Name + " / " + group.Name)).ToArray();
        _historyGroup.DataSource = choices.Length == 0 ? new[] { new Choice(null, "尚无已启用的标准点行写入组") } : choices;
        SelectChoice(_historyGroup, selected);
        UpdateHistoryQueryState();
    }

    private void BindHistoryTags()
    {
        var prior = (_historyTag.SelectedItem as Choice)?.Id;
        var deviceId = (_historyDevice.SelectedItem as Choice)?.Id;
        var tags = _configuration.Devices.Where(x => !deviceId.HasValue || x.Id == deviceId)
            .SelectMany(d => d.Tags.Select(t => new Choice(t.Id, deviceId.HasValue ? t.Name : d.Name + " / " + t.Name)));
        _historyTag.DataSource = new[] { new Choice(null, "全部点位（无趋势）") }.Concat(tags).ToArray();
        SelectChoice(_historyTag, prior);
    }

    private void RefreshRealtime(IReadOnlyList<TelemetrySample> samples)
    {
        if (_writeButton is null) return;
        var deviceId = (_realtimeDevice.SelectedItem as Choice)?.Id;
        var search = _realtimeSearch.Text.Trim();
        var rows = samples.Where(sample => (!deviceId.HasValue || sample.DeviceId == deviceId)
            && (search.Length == 0 || sample.TagName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || sample.Address.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(sample => sample.DeviceName).ThenBy(sample => sample.TagName).Select(ToRow).ToList();
        GridViewState.UpdateRows(_realtimeGrid, rows, row => (row.Sample.DeviceId, row.Sample.TagId));
        UpdateWriteButton();
    }

    private void UpdateWriteButton()
    {
        if (_writeButton is null) return;
        var sample = (_realtimeGrid.CurrentRow?.DataBoundItem as SampleRow)?.Sample;
        var device = _configuration.Devices.FirstOrDefault(x => x.Id == sample?.DeviceId);
        var tag = device?.Tags.FirstOrDefault(x => x.Id == sample?.TagId);
        _writeButton.Enabled = !_busy && !_closing && device is not null && _application.IsDeviceRunning(device.Id) && device.Enabled && tag?.Enabled == true && tag.Writable;
    }

    private async Task WritePointAsync()
    {
        var sample = (_realtimeGrid.CurrentRow?.DataBoundItem as SampleRow)?.Sample;
        if (sample is null) return;
        var tag = _configuration.Devices.First(d => d.Id == sample.DeviceId).Tags.First(t => t.Id == sample.TagId);
        if (!tag.Enabled || !tag.Writable || !_application.IsDeviceRunning(sample.DeviceId)) throw new InvalidOperationException("该点位未启用写入，或采集尚未启动。");
        var current = sample.Value switch
        {
            null => "", double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            float number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture), _ => ValueText(sample.Value)
        };
        using var dialog = new WriteValueDialog(sample.DeviceName, sample.TagName, sample.Unit, current, tag.DataType);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(this, $"确认向设备“{sample.DeviceName}”的点位“{sample.TagName}”写入工程值 {dialog.Value} {sample.Unit}？\n\n此操作会发送实际设备写入命令。", "确认设备写入", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        var result = await _application.WriteVerifiedAsync(sample.DeviceId, sample.TagId, dialog.Value, dialog.VerificationOptions);
        var state = result.State switch
        {
            WriteCommandState.Matched => "写入后读回一致", WriteCommandState.Submitted => "已提交写入（未执行读回）",
            WriteCommandState.Mismatched => "已提交，但读回值不一致", WriteCommandState.TimedOut => "操作超时，写入状态见详情",
            WriteCommandState.Cancelled => "操作已取消，写入状态见详情", _ => "写入或核对失败"
        };
        var details = $"结果：{state}\n命令编号：{result.CommandId}\n已调用设备写入：{result.WriteAttempted}\n驱动确认提交：{result.Submitted}\n期望工程值：{ValueText(result.ExpectedEngineeringValue)}\n读回次数：{result.ReadBackAttempts}\n耗时：{result.Elapsed.TotalMilliseconds:F1} ms";
        if (result.ReadBack is { } read) details += $"\n读回工程值：{ValueText(read.Value)}\n质量：{read.Quality}\n采样时间：{read.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}";
        if (!string.IsNullOrEmpty(result.Error)) details += "\n说明：" + result.Error;
        SetFooter(state + " · " + sample.TagName);
        MessageBox.Show(this, details, "写入与读回结果", MessageBoxButtons.OK,
            result.State is WriteCommandState.Matched or WriteCommandState.Submitted ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task QueryHistoryAsync()
    {
        if (_historyFrom.Value >= _historyTo.Value) throw new InvalidOperationException("开始时间必须早于结束时间。");
        var deviceId = (_historyDevice.SelectedItem as Choice)?.Id;
        var tagId = (_historyTag.SelectedItem as Choice)?.Id;
        var fromLogs = HistoryFromLogs;
        var scanLimit = (int)_historyScanLimit.Value;
        var groupId = fromLogs ? null : (_historyGroup.SelectedItem as Choice)?.Id;
        if (!fromLogs && !groupId.HasValue) throw new InvalidOperationException("请先配置并启用外部数据库的标准点行写入组。");
        var queriedGroupName = _historyGroup.Text;
        var queriedDeviceName = _historyDevice.Text;
        var queriedTagName = _historyTag.Text;
        var query = new HistoryQuery(deviceId, tagId, new DateTimeOffset(_historyFrom.Value), new DateTimeOffset(_historyTo.Value), (int)_historyLimit.Value);
        if (fromLogs)
        {
            var result = await _application.QuerySampleLogHistoryAsync(query, scanLimit);
            PresentHistoryResults(null, "采样日志", queriedDeviceName, queriedTagName, query, result.Samples, result, scanLimit);
            SetFooter(result.IsPartial ? "采样日志查询完成，结果受限，请查看查询说明。" : "采样日志查询完成。");
        }
        else
        {
            var samples = await _application.QueryExternalHistoryAsync(groupId!.Value, query);
            PresentHistoryResults(groupId, queriedGroupName, queriedDeviceName, queriedTagName, query, samples);
            SetFooter("外部数据库历史查询完成。");
        }
    }

    private void PresentHistoryResults(Guid? groupId, string groupName, string deviceName, string tagName, HistoryQuery query, IReadOnlyList<TelemetrySample> samples, SampleLogHistoryResult? logResult = null, int scanLimit = 32)
    {
        _queriedHistory = samples;
        var rows = _queriedHistory.Select(ToRow).ToList();
        GridViewState.Rebind(_historyGrid, rows, row => row, rows.FirstOrDefault());
        _executedHistoryQuery = query; _executedHistoryGroupId = groupId;
        _executedHistoryFromLogs = logResult is not null;
        _executedHistoryScanLimit = scanLimit;
        var tagId = query.TagId;
        _trend.Visible = tagId.HasValue;
        _trend.SetSamples(tagId.HasValue ? _queriedHistory : [], tagId.HasValue ? tagName : "");
        var limited = _queriedHistory.Count == query.Limit ? "；已达到条数上限，可缩小时间范围" : "";
        _executedHistorySummary = $"{groupName} · {deviceName} / {tagName}  ·  {query.From:MM-dd HH:mm} — {query.To:MM-dd HH:mm}  ·  {_queriedHistory.Count:F0} 条{limited}。";
        if (logResult is not null)
        {
            _executedHistorySummary += $" 返回上限 {query.Limit} 条；扫描 {logResult.ScannedBytes / 1048576D:F2} M（上限 {scanLimit} M），损坏 {logResult.InvalidLines} 条。"
                + (logResult.IsPartial ? "【受限结果】" : "")
                + "仅分析仍保留的日志；日志轮转、清理或未记录的时段无法补回，不代表完整历史。"
                + string.Join("；", logResult.Warnings);
            if (samples.Count == 0) _executedHistorySummary += " 未找到匹配采样，请检查时间、筛选条件及采样日志开关和保留范围。";
        }
        UpdateHistoryQueryState();
    }

    private void UpdateHistoryQueryState()
    {
        if (_historyExport is null) return;
        var current = new HistoryQuery((_historyDevice.SelectedItem as Choice)?.Id, (_historyTag.SelectedItem as Choice)?.Id,
            new DateTimeOffset(_historyFrom.Value), new DateTimeOffset(_historyTo.Value), (int)_historyLimit.Value);
        var source = (_historyGroup.SelectedItem as Choice)?.Id;
        var fromLogs = HistoryFromLogs;
        _historyPageView.groupSelectorFilter.Visible = !fromLogs;
        _historyPageView.scanLimitFilter.Visible = fromLogs;
        _historyConditionsChanged = _executedHistoryQuery is not null && (current != _executedHistoryQuery
            || fromLogs != _executedHistoryFromLogs
            || (fromLogs ? (int)_historyScanLimit.Value != _executedHistoryScanLimit : source != _executedHistoryGroupId));
        if (_historyQuery is not null) _historyQuery.Enabled = !_busy && !_closing && (fromLogs || source.HasValue);
        _historySummary.Text = _executedHistoryQuery is null ? fromLogs
            ? "查询本地保留的采样日志，无需数据库连接。选择单个数值点位查看趋势；坏质量或非数值采样不连线。"
            : source.HasValue ? "选择查询条件后点击“查询”。可按单个点位查看趋势。" : "尚未配置可查询的数据库。请在“数据库”页启用连接与标准点行写入组；默认采集结果保存在日志文件。"
            : (_historyConditionsChanged ? "查询条件已修改，请重新查询。下方仍为上次结果：\n" : "查询结果：") + _executedHistorySummary;
        _historySummary.ForeColor = _historyConditionsChanged ? UiTheme.Warning : UiTheme.Muted;
        _historyExport.Text = _historyConditionsChanged ? "导出上次结果" : "导出查询结果";
        _historyExport.Enabled = !_busy && !_closing && _queriedHistory.Count > 0;
    }

    private async Task ExportHistoryAsync()
    {
        if (_queriedHistory.Count == 0) throw new InvalidOperationException("请先查询有数据的历史记录。");
        using var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = "edge-history-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv", Title = "导出本次历史查询", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await File.WriteAllTextAsync(dialog.FileName, BuildHistoryCsv(), new UTF8Encoding(true));
        SetFooter($"已导出 {_queriedHistory.Count:F0} 条历史记录。");
    }

    private string BuildHistoryCsv()
    {
        var text = new StringBuilder();
        if (_executedHistoryFromLogs && _executedHistoryQuery is { } query)
            text.AppendLine(CsvLine("查询说明", _executedHistorySummary,
                "开始时间", query.From.ToString("O"), "结束时间", query.To.ToString("O"),
                "设备筛选ID", query.DeviceId?.ToString() ?? "全部", "点位筛选ID", query.TagId?.ToString() ?? "全部"));
        text.AppendLine(CsvLine("设备ID", "设备名称", "点位ID", "点位名称", "地址", "原始值", "工程值", "单位", "质量", "采样时间（含时区）", "错误信息"));
        foreach (var sample in _queriedHistory)
            text.AppendLine(CsvLine(sample.DeviceId.ToString(), sample.DeviceName, sample.TagId.ToString(), sample.TagName, sample.Address,
                ValueText(sample.RawValue), ValueText(sample.Value), sample.Unit, sample.Quality.ToString(), sample.Timestamp.ToLocalTime().ToString("O"), sample.Error ?? ""));
        return text.ToString();
    }

    private static string CsvLine(params string[] values) => string.Join(",", values.Select(value =>
    {
        var text = value;
        if (text.Length > 0 && ("=+-@\t\r\n".Contains(text[0]) || "=+-@".Contains(text.TrimStart().FirstOrDefault()))) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }));
    private static string LogLevelText(GatewayLogLevel level) => level switch { GatewayLogLevel.Debug => "调试", GatewayLogLevel.Information => "信息", GatewayLogLevel.Warning => "警告", _ => "错误" };
    private static SampleRow ToRow(TelemetrySample sample) => new(sample, sample.DeviceName, sample.TagName, sample.Address, ValueText(sample.Value), ValueText(sample.RawValue), sample.Unit, QualityText(sample.Quality), sample.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss.fff"), sample.Error ?? "");
    private static void SampleCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is DataGridView grid && e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is SampleRow row && grid.Columns[e.ColumnIndex].Name == "Quality")
            e.CellStyle!.ForeColor = row.Sample.Quality == SampleQuality.Good ? UiTheme.Green : row.Sample.Quality == SampleQuality.Bad ? UiTheme.Red : Color.FromArgb(180, 83, 9);
    }
    private sealed record SampleRow(TelemetrySample Sample, string Device, string Tag, string Address, string Value, string RawValue, string Unit, string Quality, string Time, string Error);
}
