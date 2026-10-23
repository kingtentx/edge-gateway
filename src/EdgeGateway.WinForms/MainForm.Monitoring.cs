using EdgeGateway.Model;
using System.Globalization;
using System.Text;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly DataGridView _realtimeGrid = UiTheme.Grid();
    private readonly DataGridView _historyGrid = UiTheme.Grid();
    private readonly DataGridView _logGrid = UiTheme.Grid();
    private readonly ComboBox _realtimeDevice = ChoiceBox(200);
    private readonly TextBox _realtimeSearch = new() { PlaceholderText = "搜索点位名称 / 地址", Width = 220, Margin = new Padding(0, 4, 8, 0) };
    private readonly ComboBox _historyDevice = ChoiceBox(180);
    private readonly ComboBox _historyTag = ChoiceBox(220);
    private readonly DateTimePicker _historyFrom = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 190, Value = DateTime.Now.AddHours(-1), Margin = new Padding(0, 4, 8, 0) };
    private readonly DateTimePicker _historyTo = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 190, Value = DateTime.Now.AddMinutes(1), Margin = new Padding(0, 4, 8, 0) };
    private readonly NumericUpDown _historyLimit = new() { Minimum = 100, Maximum = 10000, Value = 5000, Increment = 100, Width = 90, Margin = new Padding(0, 4, 8, 0) };
    private readonly ComboBox _logLevel = ChoiceBox(150);
    private readonly TextBox _logSearch = new() { PlaceholderText = "搜索日志内容", Width = 250, Margin = new Padding(0, 4, 8, 0) };
    private readonly TrendChart _trend = new() { Dock = DockStyle.Fill };
    private readonly Label _historySummary = UiTheme.Label("选择时间范围并查询，最多返回 10,000 条。", 9, UiTheme.Muted);
    private IReadOnlyList<TelemetrySample> _queriedHistory = [];
    private Button _writeButton = null!;

    private Control BuildRealtimePage()
    {
        ConfigureSampleColumns(_realtimeGrid, includeRaw: true);
        _realtimeGrid.CellFormatting += SampleCellFormatting;
        _realtimeGrid.SelectionChanged += (_, _) => UpdateWriteButton();
        _realtimeDevice.SelectedIndexChanged += (_, _) => RefreshRealtime(_runtime.GetLatest());
        _realtimeSearch.TextChanged += (_, _) => RefreshRealtime(_runtime.GetLatest());
        _writeButton = UiTheme.Button("写入选中点位", async (_, _) => await RunBusyAsync(WritePointAsync));
        var toolbar = UiTheme.Toolbar(UiTheme.Label("设备", 10, UiTheme.Muted), _realtimeDevice, _realtimeSearch, _writeButton);
        return UiTheme.PageLayout("实时监控", "显示最新工程值、原始值和采样质量；只有配置为可写的点位才能手动写入。", toolbar, _realtimeGrid);
    }

    private Control BuildHistoryPage()
    {
        ConfigureSampleColumns(_historyGrid, includeRaw: true);
        _historyGrid.CellFormatting += SampleCellFormatting;
        _historyDevice.SelectedIndexChanged += (_, _) => BindHistoryTags();
        var query = UiTheme.Button("查询", async (_, _) => await RunBusyAsync(QueryHistoryAsync), true);
        var export = UiTheme.Button("导出本次查询", async (_, _) => await RunBusyAsync(ExportHistoryAsync));
        var recent = UiTheme.Button("最近一小时", (_, _) => { _historyTo.Value = DateTime.Now; _historyFrom.Value = DateTime.Now.AddHours(-1); });
        var toolbar = UiTheme.Toolbar(_historyDevice, _historyTag, _historyFrom, UiTheme.Label("至", 10, UiTheme.Muted), _historyTo,
            UiTheme.Label("条数", 10, UiTheme.Muted), _historyLimit, query, recent, export);
        var content = new TableLayoutPanel { Name = "HistoryContent", ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _trend.Name = "HistoryTrend"; _historySummary.Dock = DockStyle.Fill;
        content.Controls.Add(_trend, 0, 0); content.Controls.Add(_historySummary, 0, 1); content.Controls.Add(_historyGrid, 0, 2);
        void ArrangeSummary() { if (IsDpiLayoutReady) SetWrappingWidth(_historySummary, content.ClientSize.Width); }
        content.SizeChanged += (_, _) => ArrangeSummary();
        DpiLayoutCompleted += (_, _) => ArrangeSummary();
        var viewport = ScrollableContent(content, "HistoryViewport", () => UiTheme.Scale(content, 405) + _historySummary.PreferredHeight);
        return UiTheme.PageLayout("历史与趋势", "按本地时间查询 SQLite 采样历史，选择单个点位查看趋势；CSV 导出当前查询结果。", toolbar, viewport);
    }

    private Control BuildLogsPage()
    {
        UiTheme.AddColumn(_logGrid, "Time", "时间", 165, 90);
        UiTheme.AddColumn(_logGrid, "Level", "级别", 80, 45);
        UiTheme.AddColumn(_logGrid, "Message", "日志内容", 300, 400);
        _logLevel.Items.AddRange(new object[] { "全部级别", "Information", "Warning", "Error", "Debug" });
        _logLevel.SelectedIndex = 0;
        _logLevel.SelectedIndexChanged += (_, _) => RefreshLogs();
        _logSearch.TextChanged += (_, _) => RefreshLogs();
        _logGrid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _logGrid.Rows[e.RowIndex].DataBoundItem is LogRow row)
                e.CellStyle!.ForeColor = row.Level == "错误" ? UiTheme.Red : row.Level == "警告" ? Color.FromArgb(180, 83, 9) : UiTheme.Ink;
        };
        var export = UiTheme.Button("导出日志", async (_, _) => await RunBusyAsync(ExportLogsAsync));
        return UiTheme.PageLayout("运行日志", "查看最近 500 条运行事件、连接异常、报警变化和手动写入记录。", UiTheme.Toolbar(_logLevel, _logSearch, export), _logGrid);
    }

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
        var selectedId = (_realtimeGrid.CurrentRow?.DataBoundItem as SampleRow)?.Sample.TagId;
        var deviceId = (_realtimeDevice.SelectedItem as Choice)?.Id;
        var search = _realtimeSearch.Text.Trim();
        _realtimeGrid.DataSource = samples.Where(s => (!deviceId.HasValue || s.DeviceId == deviceId)
            && (search.Length == 0 || s.TagName.Contains(search, StringComparison.OrdinalIgnoreCase) || s.Address.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.DeviceName).ThenBy(s => s.TagName).Select(ToRow).ToList();
        if (selectedId.HasValue)
            foreach (DataGridViewRow row in _realtimeGrid.Rows)
                if (row.DataBoundItem is SampleRow sample && sample.Sample.TagId == selectedId.Value) { _realtimeGrid.CurrentCell = row.Cells[0]; break; }
        UpdateWriteButton();
    }

    private void UpdateWriteButton()
    {
        if (_writeButton is null) return;
        var sample = (_realtimeGrid.CurrentRow?.DataBoundItem as SampleRow)?.Sample;
        var device = _configuration.Devices.FirstOrDefault(x => x.Id == sample?.DeviceId);
        var tag = device?.Tags.FirstOrDefault(x => x.Id == sample?.TagId);
        _writeButton.Enabled = !_busy && !_closing && device is not null && _runtime.IsDeviceRunning(device.Id) && device.Enabled && tag?.Enabled == true && tag.Writable;
    }

    private async Task WritePointAsync()
    {
        var sample = (_realtimeGrid.CurrentRow?.DataBoundItem as SampleRow)?.Sample;
        if (sample is null) return;
        var tag = _configuration.Devices.First(d => d.Id == sample.DeviceId).Tags.First(t => t.Id == sample.TagId);
        if (!tag.Enabled || !tag.Writable || !_runtime.IsDeviceRunning(sample.DeviceId)) throw new InvalidOperationException("该点位未启用写入，或采集尚未启动。");
        var current = sample.Value switch
        {
            null => "", double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            float number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture), _ => ValueText(sample.Value)
        };
        using var dialog = new WriteValueDialog(sample.DeviceName, sample.TagName, sample.Unit, current, tag.DataType);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(this, $"确认向设备“{sample.DeviceName}”的点位“{sample.TagName}”写入工程值 {dialog.Value} {sample.Unit}？\n\n此操作会发送实际设备写入命令。", "确认设备写入", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        var result = await _runtime.WriteVerifiedAsync(sample.DeviceId, sample.TagId, dialog.Value, dialog.VerificationOptions);
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
        var query = new HistoryQuery(deviceId, tagId, new DateTimeOffset(_historyFrom.Value), new DateTimeOffset(_historyTo.Value), (int)_historyLimit.Value);
        _queriedHistory = await _store.QueryHistoryAsync(query);
        _historyGrid.DataSource = _queriedHistory.Select(ToRow).ToList();
        _trend.SetSamples(tagId.HasValue ? _queriedHistory : [], tagId.HasValue ? _historyTag.Text : "选择单个点位后查询，查看数值趋势");
        var limited = _queriedHistory.Count == query.Limit ? "；已达到条数上限，请缩小时间范围查看更完整数据" : "";
        _historySummary.Text = $"共 {_queriedHistory.Count:N0} 条，按时间从新到旧排列{limited}。";
        SetFooter("历史查询完成。");
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

    private void RefreshLogs()
    {
        var level = _logLevel.SelectedIndex > 0 ? _logLevel.SelectedItem?.ToString() : null;
        var search = _logSearch.Text.Trim();
        _logGrid.DataSource = _log.GetEntries(500).Where(x => (level is null || x.Level.ToString() == level)
            && (search.Length == 0 || x.Message.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Timestamp).Select(x => new LogRow(x.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss.fff"), LogLevelText(x.Level), x.Message)).ToList();
    }

    private async Task ExportLogsAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "日志文件 (*.txt)|*.txt", FileName = "edge-logs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var lines = _log.GetEntries(500).Select(x => $"{x.Timestamp.ToLocalTime():O} [{x.Level}] {x.Message}");
        await File.WriteAllLinesAsync(dialog.FileName, lines, new UTF8Encoding(true));
        SetFooter("最近 500 条日志已导出。");
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
    private sealed record LogRow(string Time, string Level, string Message);
}
