using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly DataGridView _archiveGrid = UiTheme.Grid();
    private readonly DateTimePicker _archiveFrom = new() { Name = "ArchiveFrom", Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 200, Value = DateTime.Now.AddHours(-1) };
    private readonly DateTimePicker _archiveTo = new() { Name = "ArchiveTo", Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 200, Value = DateTime.Now.AddMinutes(1) };
    private readonly TextBox _archiveText = new() { Name = "ArchiveKeyword", PlaceholderText = "关键词（最多 256 字符）", MaxLength = 256, Width = 220 };
    private readonly ComboBox _archiveLevel = new() { Name = "ArchiveLevel", DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, FormattingEnabled = true };
    private readonly NumericUpDown _archiveLimit = new() { Name = "ArchiveLimit", Minimum = 1, Maximum = 2000, Value = 500, Width = 85 };
    private readonly NumericUpDown _archiveBudget = new() { Name = "ArchiveBudget", Minimum = 1, Maximum = 64, Value = 32, Width = 80 };
    private readonly CheckBox _archiveSamples = new() { Name = "ArchiveSamples", Text = "包含采样", Checked = true, AutoSize = true };
    private readonly Label _archiveState = EditorLayout.WrappingLabel("查询近期日志文件；结果受扫描量、行数及 4 M 结果载荷限制，不等于完整备份。", color: UiTheme.Muted);
    private CancellationTokenSource? _archiveCancellation;
    private GatewayLogQueryResult? _archiveResult;
    private Button _archiveSearch = null!, _archiveCancel = null!, _archiveExport = null!;

    private TabPage BuildLogArchivePage()
    {
        _archiveGrid.Name = "LogArchiveResults";
        UiTheme.AddColumn(_archiveGrid, "Timestamp", "记录时间", 180, 180);
        UiTheme.AddColumn(_archiveGrid, "Level", "等级", 100);
        UiTheme.AddColumn(_archiveGrid, "Kind", "类别", 90);
        UiTheme.AddColumn(_archiveGrid, "Message", "内容", 480, 280);
        UiTheme.AddColumn(_archiveGrid, "FileName", "来源文件", 280, 180);
        _archiveLevel.Items.AddRange(Enum.GetValues<GatewayLogLevel>().Cast<object>().ToArray());
        _archiveLevel.SelectedItem = GatewayLogLevel.Information;
        _archiveLevel.Format += (_, e) => { if (e.ListItem is GatewayLogLevel value) e.Value = LogLevelText(value); };
        _archiveSearch = UiTheme.Button("查询文件", async (_, _) => await QueryArchiveAsync(), true); _archiveSearch.Name = "QueryLogArchive";
        _archiveCancel = UiTheme.Button("取消查询 / 导出", (_, _) => _archiveCancellation?.Cancel()); _archiveCancel.Name = "CancelLogArchive"; _archiveCancel.Enabled = false;
        _archiveExport = UiTheme.Button("导出当前结果", async (_, _) => await ExportArchiveAsync()); _archiveExport.Name = "ExportLogArchive"; _archiveExport.Enabled = false;
        var filters = UiTheme.Toolbar(UiTheme.Label("开始", 10), _archiveFrom, UiTheme.Label("结束", 10), _archiveTo,
            _archiveLevel, _archiveText, _archiveSamples, UiTheme.Label("最多行数", 10), _archiveLimit, UiTheme.Label("扫描 M", 10), _archiveBudget);
        filters.Name = "LogArchiveFilters";
        var actions = UiTheme.Toolbar(_archiveSearch, _archiveCancel, _archiveExport); actions.Name = "LogArchiveActions";
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _archiveState.Name = "LogArchiveStatus";
        panel.Controls.Add(filters, 0, 0); panel.Controls.Add(actions, 0, 1); panel.Controls.Add(_archiveState, 0, 2); panel.Controls.Add(_archiveGrid, 0, 3);
        panel.SizeChanged += (_, _) => SetWrappingWidth(_archiveState, panel.ClientSize.Width - 16);
        var page = new TabPage("文件查询与导出") { BackColor = Color.White };
        page.Controls.Add(ScrollableContent(panel, "LogArchiveViewport", () => filters.Height + actions.Height + _archiveState.Height + UiTheme.Scale(panel, 170)));
        FormClosed += (_, _) => _archiveCancellation?.Cancel();
        return page;
    }

    private void SetArchiveBusy(bool busy)
    {
        _archiveSearch.Enabled = !busy; _archiveExport.Enabled = !busy && _archiveResult is not null;
        _archiveCancel.Enabled = busy;
    }

    private async Task QueryArchiveAsync()
    {
        if (_archiveCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(); _archiveCancellation = cancellation; SetArchiveBusy(true);
        try
        {
            var query = new GatewayLogQuery(new DateTimeOffset(_archiveFrom.Value), new DateTimeOffset(_archiveTo.Value),
                (GatewayLogLevel)_archiveLevel.SelectedItem!, _archiveText.Text, _archiveSamples.Checked, (int)_archiveLimit.Value, (int)_archiveBudget.Value);
            _archiveState.Text = "正在查询；修改筛选项不会改变本次已提交的查询条件。";
            var result = await _application.QueryLogFilesAsync(query, cancellation.Token);
            if (IsDisposed || Disposing) return;
            _archiveResult = result;
            _archiveGrid.DataSource = result.Entries.Select(entry => new
            {
                Timestamp = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"), Level = LogLevelText(entry.Level),
                Kind = entry.Kind == "sample" ? "采样" : "事件", entry.Message, entry.FileName
            }).ToArray();
            _archiveState.Text = $"本次结果 {result.Entries.Count:N0} 条，扫描 {result.ScannedBytes / 1048576d:F2} M，损坏 {result.InvalidLines} 条。"
                + (result.IsPartial ? "【受限结果】" : "") + "导出的是这次查询的快照，不是当前尚未提交的筛选项。\n" + string.Join("；", result.Warnings);
        }
        catch (OperationCanceledException) { if (!IsDisposed) _archiveState.Text = "查询已取消，保留上一次结果。"; }
        catch (Exception exception) { if (!IsDisposed) _archiveState.Text = "查询失败，保留上一次结果：" + exception.Message; }
        finally { _archiveCancellation = null; if (!IsDisposed) SetArchiveBusy(false); }
    }

    private async Task ExportArchiveAsync()
    {
        if (_archiveCancellation is not null || _archiveResult is null) return;
        using var dialog = new SaveFileDialog { Title = "导出已查询的日志结果", Filter = "JSON 行日志 (*.jsonl)|*.jsonl", DefaultExt = "jsonl", AddExtension = true,
            FileName = "gateway-query-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var result = _archiveResult; // Export this exact executed query, even when controls have changed.
        using var cancellation = new CancellationTokenSource(); _archiveCancellation = cancellation; SetArchiveBusy(true);
        try
        {
            await _application.ExportLogQueryAsync(result, dialog.FileName, cancellation.Token);
            if (!IsDisposed) _archiveState.Text = $"已导出 {result.Entries.Count} 条查询结果（{(result.IsPartial ? "受限结果" : "已扫描范围")}）；首行包含查询条件及完整性说明。";
        }
        catch (OperationCanceledException) { if (!IsDisposed) _archiveState.Text = "导出已取消，原目标文件保持不变。"; }
        catch (Exception exception) { if (!IsDisposed) _archiveState.Text = "导出失败：" + exception.Message; }
        finally { _archiveCancellation = null; if (!IsDisposed) SetArchiveBusy(false); }
    }
}
