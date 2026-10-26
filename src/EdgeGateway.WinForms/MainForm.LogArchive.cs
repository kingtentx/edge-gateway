using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private LogArchiveView _eventArchive = null!, _sampleArchive = null!;

    private TabPage BuildLogArchivePage(bool samples)
    {
        var view = new LogArchiveView(this, samples);
        if (samples) _sampleArchive = view; else _eventArchive = view;
        return view.Page;
    }

    // Each tab owns its filters, cancellation and immutable executed result.
    private sealed class LogArchiveView
    {
        private readonly MainForm _owner;
        private readonly bool _samples;
        internal readonly DataGridView Grid = UiTheme.Grid();
        internal readonly DateTimePicker From = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 190, Value = DateTime.Now.AddHours(-1) };
        internal readonly DateTimePicker To = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 190, Value = DateTime.Now.AddMinutes(1) };
        internal readonly TextBox Keyword = new() { MaxLength = 256, Width = 180, PlaceholderText = "内容 / 地址 / 错误" };
        internal readonly TextBox Device = new() { MaxLength = 256, Width = 160, PlaceholderText = "设备名称或完整标识" };
        internal readonly TextBox Tag = new() { MaxLength = 256, Width = 160, PlaceholderText = "点位名称或完整标识" };
        internal readonly ComboBox Level = ChoiceBox(110), Quality = ChoiceBox(110);
        internal readonly NumericUpDown Limit = new() { Minimum = 1, Maximum = 2000, Value = 500, Width = 80 };
        internal readonly NumericUpDown Budget = new() { Minimum = 1, Maximum = 64, Value = 32, Width = 80 };
        internal readonly Label State = EditorLayout.WrappingLabel("选择条件后查询；只查询本类日志。结果受扫描量、行数及 4 M 载荷限制。", color: UiTheme.Muted);
        internal readonly Button Search, Cancel, Export;
        internal readonly TabPage Page;
        internal GatewayLogQueryResult? Result;
        private CancellationTokenSource? _cancellation;
        private string _summary = "尚未查询。";
        private bool _changed;
        internal IReadOnlyList<Control> Inputs { get; }

        internal LogArchiveView(MainForm owner, bool samples)
        {
            _owner = owner; _samples = samples;
            var prefix = samples ? "SampleArchive" : "EventArchive";
            Page = new TabPage(samples ? "采样数据" : "运行日志") { Name = prefix, BackColor = Color.White };
            Grid.Name = prefix + "Results";
            UiTheme.AddColumn(Grid, "Timestamp", "时间", 180, 180);
            if (samples)
            {
                foreach (var (key, title) in new[] { ("Device", "设备"), ("Tag", "点位"), ("Address", "地址"), ("RawValue", "原始值"),
                    ("Value", "工程值"), ("Quality", "质量"), ("Unit", "单位"), ("Error", "错误说明") })
                    UiTheme.AddColumn(Grid, key, title, key == "Error" ? 240 : 140, key == "Error" ? 180 : 100);
            }
            else
            {
                UiTheme.AddColumn(Grid, "Level", "级别", 100, 80);
                UiTheme.AddColumn(Grid, "Message", "日志内容", 500, 280);
            }
            UiTheme.AddColumn(Grid, "FileName", "来源文件", 200, 160);
            Level.FormattingEnabled = true;
            Level.Items.AddRange(Enum.GetValues<GatewayLogLevel>().Cast<object>().ToArray());
            Level.SelectedItem = GatewayLogLevel.Debug;
            Level.Format += (_, e) => { if (e.ListItem is GatewayLogLevel value) e.Value = LogLevelText(value); };
            Quality.Items.Add("全部质量");
            Quality.Items.AddRange(Enum.GetValues<SampleQuality>().Cast<object>().ToArray());
            Quality.SelectedIndex = 0; Quality.FormattingEnabled = true;
            Quality.Format += (_, e) => { if (e.ListItem is SampleQuality value) e.Value = QualityText(value); };
            Search = UiTheme.Button("查询", async (_, _) => await QueryAsync(), true);
            Cancel = UiTheme.Button("取消查询 / 导出", (_, _) => _cancellation?.Cancel()); Cancel.Enabled = false;
            Export = UiTheme.Button("导出当前结果", async (_, _) => await ExportAsync()); Export.Enabled = false;
            var recent = UiTheme.Button("最近一小时", (_, _) => { To.Value = DateTime.Now; From.Value = To.Value.AddHours(-1); });
            var filters = new List<Control> { UiTheme.FilterField("开始时间", From), UiTheme.FilterField("结束时间", To) };
            if (samples) filters.AddRange([UiTheme.FilterField("设备", Device), UiTheme.FilterField("点位", Tag), UiTheme.FilterField("采样质量", Quality)]);
            else filters.Add(UiTheme.FilterField("最低级别", Level));
            filters.AddRange([UiTheme.FilterField("关键词", Keyword), UiTheme.FilterField("最多条数", Limit), UiTheme.FilterField("扫描上限（M）", Budget)]);
            var filterBar = UiTheme.Toolbar(filters.ToArray());
            var actions = UiTheme.Toolbar(Search, recent, Cancel, Export);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 3; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.Controls.Add(filterBar, 0, 0); panel.Controls.Add(actions, 0, 1); panel.Controls.Add(State, 0, 2); panel.Controls.Add(Grid, 0, 3);
            panel.SizeChanged += (_, _) => SetWrappingWidth(State, panel.ClientSize.Width - 16);
            Page.Controls.Add(owner.ScrollableContent(panel, prefix + "Viewport", () => filterBar.Height + actions.Height + State.Height + UiTheme.Scale(panel, 180)));
            Inputs = samples ? [From, To, Device, Tag, Quality, Keyword, Limit, Budget, Search, Export]
                : [From, To, Level, Keyword, Limit, Budget, Search, Export];
            foreach (var (control, name) in new[] { ((Control)From, "From"), (To, "To"), (Device, "Device"), (Tag, "Tag"), (Quality, "Quality"),
                (Level, "Level"), (Keyword, "Keyword"), (Limit, "Limit"), (Budget, "Budget"), (Search, "Search"), (Cancel, "Cancel"), (Export, "Export") }) control.Name = prefix + name;
            void Changed() { _changed = true; UpdateSummary(); }
            From.ValueChanged += (_, _) => Changed(); To.ValueChanged += (_, _) => Changed();
            Level.SelectedIndexChanged += (_, _) => Changed(); Quality.SelectedIndexChanged += (_, _) => Changed();
            foreach (var text in new[] { Device, Tag, Keyword }) text.TextChanged += (_, _) => Changed();
            Limit.ValueChanged += (_, _) => Changed(); Budget.ValueChanged += (_, _) => Changed();
            owner.FormClosed += (_, _) => _cancellation?.Cancel();
            Grid.CellDoubleClick += (_, e) =>
            {
                if (e.RowIndex >= 0 && Result is not null)
                    MessageBox.Show(owner, Result.Entries[e.RowIndex].Json, samples ? "采样详情" : "运行日志详情", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
        }

        private GatewayLogQuery Capture() => new(new DateTimeOffset(From.Value), new DateTimeOffset(To.Value),
            _samples ? GatewayLogLevel.Debug : (GatewayLogLevel)Level.SelectedItem!, Keyword.Text, _samples, (int)Limit.Value, (int)Budget.Value)
        { Category = _samples ? GatewayLogCategory.Samples : GatewayLogCategory.Events,
            Device = _samples ? Device.Text.Trim() : "", Tag = _samples ? Tag.Text.Trim() : "",
            Quality = _samples && Quality.SelectedItem is SampleQuality quality ? quality : null };

        private void Busy(bool value)
        { Search.Enabled = !value; Export.Enabled = !value && Result is not null; Cancel.Enabled = value; }
        private void UpdateSummary() => State.Text = (_changed ? "条件已修改，需重新查询；导出仍使用上次结果。\n" : "") + _summary;

        internal async Task QueryAsync()
        {
            if (_cancellation is not null) return;
            using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; Busy(true);
            var query = Capture();
            try
            {
                State.Text = "正在查询…";
                var result = await _owner._application.QueryLogFilesAsync(query, cancellation.Token);
                if (_owner.IsDisposed || _owner.Disposing) return;
                Result = result; _changed = Capture() != query;
                Grid.DataSource = result.Entries.Select(entry => new
                {
                    Timestamp = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"), Level = LogLevelText(entry.Level),
                    entry.Message, entry.FileName, Device = entry.Sample?.DeviceName, Tag = entry.Sample?.TagName,
                    Address = entry.Sample?.Address, RawValue = entry.Sample?.RawValue, Value = entry.Sample?.Value,
                    Quality = entry.Sample is null ? "" : QualityText(entry.Sample.Quality), Unit = entry.Sample?.Unit, Error = entry.Sample?.Error
                }).ToArray();
                _summary = $"{query.From.ToLocalTime():MM-dd HH:mm:ss} 至 {query.To.ToLocalTime():MM-dd HH:mm:ss} · {result.Entries.Count:N0} 条 · 扫描 {result.ScannedBytes / 1048576d:F2} M · 损坏 {result.InvalidLines} 条。"
                    + (result.IsPartial ? "【受限结果】" : "") + "导出本次查询快照。" + string.Join("；", result.Warnings);
                UpdateSummary();
            }
            catch (OperationCanceledException) { if (!_owner.IsDisposed) State.Text = "查询已取消，保留上一次结果。"; }
            catch (Exception exception) { if (!_owner.IsDisposed) State.Text = "查询失败，保留上一次结果：" + exception.Message; }
            finally { _cancellation = null; if (!_owner.IsDisposed) Busy(false); }
        }

        private async Task ExportAsync()
        {
            if (_cancellation is not null || Result is null) return;
            using var dialog = new SaveFileDialog { Title = _samples ? "导出采样查询结果" : "导出运行日志查询结果",
                Filter = "CSV 表格 (*.csv)|*.csv|原始 JSON 行日志 (*.jsonl)|*.jsonl", DefaultExt = "csv", AddExtension = true,
                FileName = (_samples ? "samples-" : "events-") + DateTime.Now.ToString("yyyyMMdd-HHmmss"), OverwritePrompt = true };
            if (dialog.ShowDialog(_owner) != DialogResult.OK) return;
            var result = Result;
            using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; Busy(true);
            try
            {
                await _owner._application.ExportLogQueryAsync(result, dialog.FileName, cancellation.Token);
                if (!_owner.IsDisposed) State.Text = $"已导出 {result.Entries.Count} 条{(result.IsPartial ? "受限" : "查询")}结果；导出文件包含已执行的查询条件。";
            }
            catch (OperationCanceledException) { if (!_owner.IsDisposed) State.Text = "导出已取消，原目标文件保持不变。"; }
            catch (Exception exception) { if (!_owner.IsDisposed) State.Text = "导出失败：" + exception.Message; }
            finally { _cancellation = null; if (!_owner.IsDisposed) Busy(false); }
        }
    }
}
