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
        ConfigureSampleColumns(_realtimeGrid, includeRaw: true);
        _realtimeGrid.CellFormatting += SampleCellFormatting;
        _realtimeGrid.SelectionChanged += (_, _) => UpdateWriteButton();
        _realtimeDevice.SelectedIndexChanged += (_, _) => RefreshRealtime(_application.GetLatest());
        _realtimeSearch.TextChanged += (_, _) => RefreshRealtime(_application.GetLatest());
        _writeButton = UiTheme.Button("写入选中点位", async (_, _) => await RunBusyAsync(WritePointAsync));
        _realtimeGrid.Name = "RealtimeValues";
        _writeButton.Margin = new Padding(0, 23, 0, 6);
        var toolbar = UiTheme.Toolbar(UiTheme.FilterField("采集设备", _realtimeDevice),
            UiTheme.FilterField("查找点位", _realtimeSearch), _writeButton);
        _realtimePageView.ToolbarHost.Controls.Add(toolbar);
        return _realtimePageView;
    }

    private Control BuildHistoryPage()
    {
        _historyPageView = new HistoryPageView();
        _historyGrid = _historyPageView.HistoryGrid;
        _historyGroup = _historyPageView.GroupSelector;
        _historyDevice = _historyPageView.DeviceSelector;
        _historyTag = _historyPageView.PointSelector;
        _historyFrom = _historyPageView.FromTime;
        _historyTo = _historyPageView.ToTime;
        _historyLimit = _historyPageView.ResultLimit;
        _historySummary = _historyPageView.QuerySummary;
        _trend = _historyPageView.TrendChart;
        ConfigureSampleColumns(_historyGrid, includeRaw: true);
        _historyGrid.CellFormatting += SampleCellFormatting;
        _historyDevice.SelectedIndexChanged += (_, _) => BindHistoryTags();
        var query = _historyQuery = UiTheme.Button("查询", async (_, _) => await RunBusyAsync(QueryHistoryAsync), true); query.Name = "QueryExternalHistory";
        _historyGroup.Name = "ExternalHistoryGroup"; _historyGroup.SelectedIndexChanged += (_, _) => UpdateHistoryQueryState();
        _historyExport = UiTheme.Button("导出查询结果", async (_, _) => await RunBusyAsync(ExportHistoryAsync));
        var recent = UiTheme.Button("最近一小时", (_, _) => { _historyTo.Value = DateTime.Now; _historyFrom.Value = DateTime.Now.AddHours(-1); });
        foreach (var button in new[] { query, recent, _historyExport }) button.Margin = new Padding(0, 23, 8, 6);
        var toolbar = UiTheme.Toolbar(UiTheme.FilterField("外部数据库写入组", _historyGroup), UiTheme.FilterField("设备", _historyDevice), UiTheme.FilterField("点位", _historyTag),
            UiTheme.FilterField("开始时间", _historyFrom), UiTheme.FilterField("结束时间", _historyTo),
            UiTheme.FilterField("最多条数", _historyLimit), query, recent, _historyExport);
        _historyDevice.SelectedIndexChanged += (_, _) => UpdateHistoryQueryState();
        _historyTag.SelectedIndexChanged += (_, _) => UpdateHistoryQueryState();
        _historyFrom.ValueChanged += (_, _) => UpdateHistoryQueryState();
        _historyTo.ValueChanged += (_, _) => UpdateHistoryQueryState();
        _historyLimit.ValueChanged += (_, _) => UpdateHistoryQueryState();
        _historyPageView.ToolbarHost.Controls.Add(toolbar);
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

    private static ComboBox ChoiceBox(int width)
    {
        var choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(0, 4, 8, 0), IntegralHeight = false, DropDownHeight = 320 };
        choice.DropDown += (_, _) =>
        {
            var workArea = Screen.FromControl(choice).WorkingArea;
            choice.DropDownHeight = Math.Min(UiTheme.Scale(choice, 320), Math.Max(choice.ItemHeight * 3, workArea.Height / 2));
            var textWidth = choice.Items.Cast<object>().Select(x => TextRenderer.MeasureText(x.ToString(), choice.Font).Width).DefaultIfEmpty(choice.Width).Max();
            choice.DropDownWidth = Math.Min(workArea.Width, Math.Max(choice.Width, textWidth + UiTheme.Scale(choice, 32)));
        };
        return choice;
    }

    private static void ConfigureSampleColumns(DataGridView grid, bool includeRaw)
    {
        UiTheme.AddColumn(grid, "Device", "设备", 100, 110);
        UiTheme.AddColumn(grid, "Tag", "点位", 110, 120);
        UiTheme.AddColumn(grid, "Address", "地址", 80, 80);
        UiTheme.AddColumn(grid, "Value", "工程值", 100, 110);
        if (includeRaw) UiTheme.AddColumn(grid, "RawValue", "原始值", 90, 100);
        UiTheme.AddColumn(grid, "Unit", "单位", 60, 60);
        UiTheme.AddColumn(grid, "Quality", "质量", 70, 65);
        UiTheme.AddColumn(grid, "Time", "采样时间", 165, 140);
        UiTheme.AddColumn(grid, "Error", "状态信息", 130, 150);
    }

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
        GridViewState.Rebind(_realtimeGrid, rows, row => (row.Sample.DeviceId, row.Sample.TagId));
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
        var groupId = (_historyGroup.SelectedItem as Choice)?.Id ?? throw new InvalidOperationException("请先配置并启用外部数据库的标准点行写入组。");
        var queriedGroupName = _historyGroup.Text;
        var queriedDeviceName = _historyDevice.Text;
        var queriedTagName = _historyTag.Text;
        var query = new HistoryQuery(deviceId, tagId, new DateTimeOffset(_historyFrom.Value), new DateTimeOffset(_historyTo.Value), (int)_historyLimit.Value);
        var samples = await _application.QueryExternalHistoryAsync(groupId, query);
        PresentHistoryResults(groupId, queriedGroupName, queriedDeviceName, queriedTagName, query, samples);
        SetFooter("外部数据库历史查询完成。");
    }

    private void PresentHistoryResults(Guid? groupId, string groupName, string deviceName, string tagName, HistoryQuery query, IReadOnlyList<TelemetrySample> samples)
    {
        _queriedHistory = samples;
        _historyGrid.DataSource = _queriedHistory.Select(ToRow).ToList();
        _executedHistoryQuery = query; _executedHistoryGroupId = groupId;
        var tagId = query.TagId;
        _trend.Visible = tagId.HasValue;
        if (_trend.Parent is TableLayoutPanel historyLayout)
            historyLayout.RowStyles[0].Height = tagId.HasValue ? UiTheme.Scale(_trend, 205) : 0;
        _trend.SetSamples(tagId.HasValue ? _queriedHistory : [], tagId.HasValue ? tagName : "");
        var limited = _queriedHistory.Count == query.Limit ? "；已达到条数上限，可缩小时间范围" : "";
        _executedHistorySummary = $"{groupName} · {deviceName} / {tagName}  ·  {query.From:MM-dd HH:mm} — {query.To:MM-dd HH:mm}  ·  {_queriedHistory.Count:N0} 条{limited}。";
        UpdateHistoryQueryState();
    }

    private void UpdateHistoryQueryState()
    {
        if (_historyExport is null) return;
        var current = new HistoryQuery((_historyDevice.SelectedItem as Choice)?.Id, (_historyTag.SelectedItem as Choice)?.Id,
            new DateTimeOffset(_historyFrom.Value), new DateTimeOffset(_historyTo.Value), (int)_historyLimit.Value);
        var source = (_historyGroup.SelectedItem as Choice)?.Id;
        _historyConditionsChanged = _executedHistoryQuery is not null && (current != _executedHistoryQuery || source != _executedHistoryGroupId);
        if (_historyQuery is not null) _historyQuery.Enabled = !_busy && !_closing && source.HasValue;
        _historySummary.Text = _executedHistoryQuery is null ? source.HasValue ? "选择查询条件后点击“查询”。可按单个点位查看趋势。" : "尚未配置可查询的数据库。请在“数据库”页启用连接与标准点行写入组；默认采集结果保存在日志文件。"
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
        var text = new StringBuilder();
        text.AppendLine(CsvLine("设备ID", "设备名称", "点位ID", "点位名称", "地址", "原始值", "工程值", "单位", "质量", "采样时间（含时区）", "错误信息"));
        foreach (var sample in _queriedHistory)
            text.AppendLine(CsvLine(sample.DeviceId.ToString(), sample.DeviceName, sample.TagId.ToString(), sample.TagName, sample.Address,
                ValueText(sample.RawValue), ValueText(sample.Value), sample.Unit, sample.Quality.ToString(), sample.Timestamp.ToLocalTime().ToString("O"), sample.Error ?? ""));
        await File.WriteAllTextAsync(dialog.FileName, text.ToString(), new UTF8Encoding(true));
        SetFooter($"已导出 {_queriedHistory.Count:N0} 条历史记录。");
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
