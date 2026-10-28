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
        internal readonly Button Search, Cancel, Export;
        internal readonly TabPage Page;
        internal GatewayLogQueryResult? Result;
        private CancellationTokenSource? _cancellation;
        private string _summary = "尚未查询。";
        private bool _changed;
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
            State = view.State; Search = view.Search; Cancel = view.Cancel; Export = view.Export;
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
            Cancel.Click += (_, _) => _cancellation?.Cancel(); Cancel.Enabled = false;
            Export.Click += async (_, _) => await ExportAsync(); Export.Enabled = false;
            view.Recent.Click += (_, _) => { To.Value = DateTime.Now; From.Value = To.Value.AddHours(-1); };
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
                var rows = result.Entries.Select((entry, index) => new
                {
                    Index = index, Timestamp = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"), Level = LogLevelText(entry.Level),
                    entry.Message, entry.FileName, Device = entry.Sample?.DeviceName, Tag = entry.Sample?.TagName,
                    Address = entry.Sample?.Address, RawValue = entry.Sample?.RawValue, Value = entry.Sample?.Value,
                    Quality = entry.Sample is null ? "" : QualityText(entry.Sample.Quality), Unit = entry.Sample?.Unit, Error = entry.Sample?.Error
                }).ToArray();
                GridViewState.Rebind(Grid, rows, row => row.Index, 0);
                _summary = $"{query.From.ToLocalTime():MM-dd HH:mm:ss} 至 {query.To.ToLocalTime():MM-dd HH:mm:ss} · {result.Entries.Count:F0} 条 · 扫描 {result.ScannedBytes / 1048576d:F2} M · 损坏 {result.InvalidLines} 条。"
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
