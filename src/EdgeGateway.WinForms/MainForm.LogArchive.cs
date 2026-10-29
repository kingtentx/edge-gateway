using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private LogArchiveView _eventArchive = null!, _sampleArchive = null!;

    private TabPage BuildLogArchivePage(bool samples)
    {
        var page = samples ? _loggingPageView.SampleLogsTab : _loggingPageView.EventLogsTab;
        var view = new LogArchiveView(this, samples, page);
        if (samples) _sampleArchive = view; else _eventArchive = view;
        return view.Page;
    }

    // Each tab owns its filters, cancellation and immutable executed result.
    private sealed class LogArchiveView
    {
        private readonly MainForm _owner;
        private readonly bool _samples;
        internal readonly GatewayTable Grid;
        internal readonly DateTimePicker From, To;
        internal readonly TextBox Keyword, Device, Tag;
        internal readonly ComboBox Level, Quality;
        internal readonly NumericUpDown Limit, Budget;
        internal readonly Label State;
        internal readonly Button Search, Cancel, Export, Live;
        internal readonly TabPage Page;
        internal GatewayLogQueryResult? Result;
        private CancellationTokenSource? _cancellation;
        private string _summary = "尚未查询。";
        private bool _changed;
        private bool _live, _bindingRange, _exporting;
        private int _revision;
        private long _nextRefresh;
        private IReadOnlyList<ArchiveRow> _rows = [];
        internal IReadOnlyList<Control> Inputs { get; }

        internal LogArchiveView(MainForm owner, bool samples, TabPage page)
        {
            _owner = owner; _samples = samples;
            var prefix = samples ? "SampleArchive" : "EventArchive";
            Page = page;
            Page.Name = prefix; Page.BackColor = Color.White;
            var view = samples ? owner._loggingPageView.sampleView : owner._loggingPageView.eventView;
            view.ConfigureCategory(samples);
            Grid = view.Grid; From = view.From; To = view.To; Device = view.Device; Tag = view.TagInput;
            Keyword = view.Keyword; Level = view.Level; Quality = view.Quality; Limit = view.Limit; Budget = view.Budget;
            State = view.State; Search = view.Search; Cancel = view.Cancel; Export = view.Export; Live = view.Live;
            Live.Name = prefix + "Live";
            _live = !samples;
            From.Value = DateTime.Now.AddHours(-1); To.Value = DateTime.Now.AddMinutes(1);
            Grid.Name = prefix + "Results";
            Level.FormattingEnabled = true;
            Level.Items.AddRange(Enum.GetValues<GatewayLogLevel>().Cast<object>().ToArray());
            Level.SelectedItem = GatewayLogLevel.Debug;
            Level.Format += (_, e) => { if (e.ListItem is GatewayLogLevel value) e.Value = LogLevelText(value); };
            Quality.Items.Add("全部质量");
            Quality.Items.AddRange(Enum.GetValues<SampleQuality>().Cast<object>().ToArray());
            Quality.SelectedIndex = 0; Quality.FormattingEnabled = true;
            Quality.Format += (_, e) => { if (e.ListItem is SampleQuality value) e.Value = QualityText(value); };
            Search.Click += async (_, _) => await QueryAsync();
            Cancel.Click += (_, _) => { SetLive(false); _cancellation?.Cancel(); }; Cancel.Enabled = false;
            Export.Click += async (_, _) => await ExportAsync(); Export.Enabled = false;
            view.Recent.Click += async (_, _) =>
            {
                To.Value = DateTime.Now; From.Value = To.Value.AddHours(-1);
                if (!_samples) { SetLive(true); await RefreshLiveAsync(); }
            };
            Live.Click += async (_, _) => { SetLive(!_live); if (_live) await RefreshLiveAsync(); };
            Inputs = samples ? [From, To, Device, Tag, Quality, Keyword, Limit, Budget, Search, Export]
                : [From, To, Level, Keyword, Limit, Budget, Search, Export];
            foreach (var (control, name) in new[] { ((Control)From, "From"), (To, "To"), (Device, "Device"), (Tag, "Tag"), (Quality, "Quality"),
                (Level, "Level"), (Keyword, "Keyword"), (Limit, "Limit"), (Budget, "Budget"), (Search, "Search"), (Cancel, "Cancel"), (Export, "Export") }) control.Name = prefix + name;
            void Changed() { if (_bindingRange) return; _revision++; _nextRefresh = 0; _changed = true; UpdateSummary(); }
            void RangeChanged() { if (_bindingRange) return; SetLive(false); Changed(); }
            From.ValueChanged += (_, _) => RangeChanged(); To.ValueChanged += (_, _) => RangeChanged();
            Level.SelectedIndexChanged += (_, _) => Changed(); Quality.SelectedIndexChanged += (_, _) => Changed();
            foreach (var text in new[] { Device, Tag, Keyword }) text.TextChanged += (_, _) => Changed();
            Limit.ValueChanged += (_, _) => Changed(); Budget.ValueChanged += (_, _) => Changed();
            owner.FormClosed += (_, _) => _cancellation?.Cancel();
            Grid.CellDoubleClick += (_, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < Grid.Rows.Count && Grid.Rows[e.RowIndex].DataBoundItem is ArchiveRow row)
                    MessageBox.Show(owner, row.Entry.Json, samples ? "采样详情" : "运行日志详情", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            Grid.CellFormatting += (_, e) =>
            {
                if (e.RowIndex < 0 || Grid.Columns[e.ColumnIndex].Name != "Level"
                    || Grid.Rows[e.RowIndex].DataBoundItem is not ArchiveRow row) return;
                e.CellStyle!.ForeColor = AntdUI.Style.Get(row.Entry.Level switch
                {
                    GatewayLogLevel.Information => AntdUI.Colour.Primary,
                    GatewayLogLevel.Warning => AntdUI.Colour.Warning,
                    GatewayLogLevel.Error => AntdUI.Colour.Error,
                    _ => AntdUI.Colour.TextTertiary
                }, nameof(AntdUI.Table));
            };
            UpdateSummary();
        }

        private void SetLive(bool live)
        {
            if (_samples) return;
            if (_live != live) _revision++;
            _live = live; _nextRefresh = 0;
            Live.Text = live ? "暂停刷新" : "实时刷新";
            UpdateSummary();
        }

        internal async Task RefreshLiveAsync()
        {
            if (_samples || !_live || _exporting || _cancellation is not null || !Page.Visible
                || _owner._closing || Environment.TickCount64 < _nextRefresh) return;
            _nextRefresh = Environment.TickCount64 + 2000;
            _bindingRange = true;
            // The picker displays whole seconds. Keep its value at that precision
            // so committing the displayed text cannot look like a user range edit.
            var now = DateTime.Now;
            try { To.Value = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)); From.Value = To.Value.AddHours(-1); }
            finally { _bindingRange = false; }
            await ExecuteQueryAsync(automatic: true);
        }

        private GatewayLogQuery Capture() => new(new DateTimeOffset(From.Value), new DateTimeOffset(To.Value),
            _samples ? GatewayLogLevel.Debug : (GatewayLogLevel)Level.SelectedItem!, Keyword.Text, _samples, (int)Limit.Value, (int)Budget.Value)
        { Category = _samples ? GatewayLogCategory.Samples : GatewayLogCategory.Events,
            Device = _samples ? Device.Text.Trim() : "", Tag = _samples ? Tag.Text.Trim() : "",
            Quality = _samples && Quality.SelectedItem is SampleQuality quality ? quality : null };

        private void Busy(bool value)
        { Search.Enabled = !value; Export.Enabled = !value && Result is not null; Cancel.Enabled = value; }
        private void UpdateSummary() => State.Text = (!_samples ? (_live ? "实时刷新 · 最近一小时 · 约每 2 秒更新。" : "已暂停实时刷新。") : "")
            + (_changed ? (_live ? "筛选条件待刷新；导出仍使用当前显示快照。\n" : "条件已修改，需重新查询；导出仍使用上次结果。\n") : "") + _summary;

        internal async Task QueryAsync()
        {
            SetLive(false);
            await ExecuteQueryAsync(automatic: false);
        }

        private async Task ExecuteQueryAsync(bool automatic)
        {
            if (_cancellation is not null) return;
            using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; Busy(true);
            var revision = _revision;
            try
            {
                var query = Capture();
                if (!automatic) State.Text = "正在查询…";
                var result = await _owner._application.QueryLogFilesAsync(query, cancellation.Token);
                if (_owner.IsDisposed || _owner.Disposing) return;
                if (automatic && (revision != _revision || !_live)) return;
                Result = result; _changed = Capture() != query;
                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                var rows = result.Entries.Select(entry =>
                {
                    // File names can change during rotation; record contents do not.
                    var identity = entry.Json;
                    occurrences.TryGetValue(identity, out var occurrence);
                    occurrences[identity] = occurrence + 1;
                    return new ArchiveRow(entry, (entry.Json, occurrence));
                }).ToArray();
                if (!automatic || !_rows.SequenceEqual(rows))
                {
                    var followTop = Grid.VerticalScrollingOffset == 0;
                    var firstIndex = Grid.FirstDisplayedScrollingRowIndex;
                    var anchor = automatic && firstIndex >= 0 && firstIndex < Grid.Rows.Count
                        ? Grid.Rows[firstIndex].DataBoundItem as ArchiveRow : null;
                    var anchorTop = anchor is null ? 0 : Grid.GetRowDisplayRectangle(firstIndex, false).Top;
                    GridViewState.Rebind(Grid, rows, row => row.Key, automatic ? null : rows.FirstOrDefault()?.Key);
                    if (automatic && followTop) Grid.VerticalScrollingOffset = 0;
                    else if (anchor is not null)
                    {
                        // AntdUI.ScrollLine reveals a row, but does not promise its
                        // former position below the fixed header. Preserve that offset.
                        var newIndex = Array.FindIndex(rows, row => row.Key == anchor.Key);
                        if (newIndex >= 0)
                            Grid.VerticalScrollingOffset += Grid.GetRowDisplayRectangle(newIndex, false).Top - anchorTop;
                    }
                    _rows = rows;
                }
                _summary = $"{query.From.ToLocalTime():MM-dd HH:mm:ss} 至 {query.To.ToLocalTime():MM-dd HH:mm:ss} · {result.Entries.Count:F0} 条 · 扫描 {result.ScannedBytes / 1048576d:F2} M · 损坏 {result.InvalidLines} 条。"
                    + (result.IsPartial ? "【受限结果】" : "") + "导出本次查询快照。" + string.Join("；", result.Warnings);
                UpdateSummary();
            }
            catch (OperationCanceledException) { if (!_owner.IsDisposed) State.Text = "查询已取消，保留上一次结果。"; }
            catch (Exception exception) { if (!_owner.IsDisposed) State.Text = "查询失败，保留上一次结果：" + exception.Message; }
            finally { _cancellation = null; _nextRefresh = Environment.TickCount64 + 2000; if (!_owner.IsDisposed) Busy(false); }
        }

        private sealed record ArchiveRow(GatewayLogFileEntry Entry, (string Json, int Occurrence) Key)
        {
            public string Timestamp => Entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
            public string Level => LogLevelText(Entry.Level);
            public string Message => Entry.Message;
            public string FileName => Entry.FileName;
            public string? Device => Entry.Sample?.DeviceName;
            public string? Tag => Entry.Sample?.TagName;
            public string? Address => Entry.Sample?.Address;
            public string? RawValue => Entry.Sample?.RawValue;
            public string? Value => Entry.Sample?.Value;
            public string Quality => Entry.Sample is null ? "" : QualityText(Entry.Sample.Quality);
            public string? Unit => Entry.Sample?.Unit;
            public string? Error => Entry.Sample?.Error;
        }

        private async Task ExportAsync()
        {
            if (_cancellation is not null || Result is null) return;
            var result = Result;
            using var dialog = new SaveFileDialog { Title = _samples ? "导出采样查询结果" : "导出运行日志查询结果",
                Filter = "CSV 表格 (*.csv)|*.csv|原始 JSON 行日志 (*.jsonl)|*.jsonl", DefaultExt = "csv", AddExtension = true,
                FileName = (_samples ? "samples-" : "events-") + DateTime.Now.ToString("yyyyMMdd-HHmmss"), OverwritePrompt = true };
            _exporting = true;
            try { if (dialog.ShowDialog(_owner) != DialogResult.OK) return; }
            finally { _exporting = false; }
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
