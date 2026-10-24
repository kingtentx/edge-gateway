using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly TabControl _databaseTabs = new EditorLayout.EditorTabs { Name = "DatabaseTabs", Dock = DockStyle.Fill, Multiline = true };
    private readonly DataGridView _databaseGrid = UiTheme.Grid(), _databaseGroupGrid = UiTheme.Grid();
    private readonly Label _databaseState = UiTheme.Label("默认采集结果写入日志。需要长期查询时，添加外部数据库并配置写入组。", 10, UiTheme.Muted);

    private Control BuildDatabasesPage()
    {
        _databaseGrid.Name = "DatabaseConnections"; _databaseGroupGrid.Name = "DatabaseWriteGroups";
        UiTheme.AddColumn(_databaseGrid, "Name", "连接名称", 150, 150); UiTheme.AddColumn(_databaseGrid, "Provider", "类型", 110, 110);
        UiTheme.AddColumn(_databaseGrid, "Target", "服务器 / 数据库", 220, 220); UiTheme.AddColumn(_databaseGrid, "State", "状态", 130, 130);
        UiTheme.AddColumn(_databaseGrid, "Queued", "排队行数", 100); UiTheme.AddColumn(_databaseGrid, "Written", "已写行数", 100);
        UiTheme.AddColumn(_databaseGrid, "Load", "队列载荷 / 峰值", 180, 180);
        UiTheme.AddColumn(_databaseGrid, "Retry", "失败 / 重试 / 超时", 180, 180);
        UiTheme.AddColumn(_databaseGrid, "Duration", "在途 / 最近耗时", 180, 180);
        UiTheme.AddColumn(_databaseGrid, "Dropped", "丢弃 / 跳过", 120); UiTheme.AddColumn(_databaseGrid, "Error", "最近错误", 260, 260);
        UiTheme.AddColumn(_databaseGroupGrid, "Name", "写入组", 150, 150); UiTheme.AddColumn(_databaseGroupGrid, "Connection", "数据库连接", 150, 150);
        UiTheme.AddColumn(_databaseGroupGrid, "Mode", "数据格式", 180, 180); UiTheme.AddColumn(_databaseGroupGrid, "Table", "目标表", 200, 200);
        UiTheme.AddColumn(_databaseGroupGrid, "Selection", "点位 / 映射", 150, 150); UiTheme.AddColumn(_databaseGroupGrid, "Enabled", "写入状态", 100);
        Button Change(string name, Func<Task> action)
        {
            var button = UiTheme.Button(name, async (_, _) => await RunBusyAsync(action));
            _stoppedConfigurationCommands.Add(button); return button;
        }
        var connections = new TabPage("数据库连接") { BackColor = Color.White };
        connections.Controls.Add(DatabasePanel(UiTheme.Toolbar(
            Change("添加连接", () => EditDatabaseConnectionAsync(true)), Change("编辑连接", () => EditDatabaseConnectionAsync(false)),
            Change("启用 / 停用连接", ToggleDatabaseConnectionAsync), Change("删除连接", DeleteDatabaseConnectionAsync),
            UiTheme.Button("测试连接", async (_, _) => await RunBusyAsync(TestSelectedDatabaseAsync))), _databaseGrid));
        var groups = new TabPage("采集写入组") { BackColor = Color.White };
        groups.Controls.Add(DatabasePanel(UiTheme.Toolbar(
            Change("添加写入组", () => EditDatabaseGroupAsync(true)), Change("编辑写入组", () => EditDatabaseGroupAsync(false)),
            Change("启用 / 停用写入组", ToggleDatabaseGroupAsync), Change("删除写入组", DeleteDatabaseGroupAsync), UiTheme.Button("检查与预览", async (_, _) => await RunBusyAsync(PreviewSelectedDatabaseGroupAsync))), _databaseGroupGrid));
        _databaseTabs.TabPages.Add(connections); _databaseTabs.TabPages.Add(groups);
        _databaseGrid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && !_application.IsRunning) await RunBusyAsync(() => EditDatabaseConnectionAsync(false)); };
        _databaseGroupGrid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && !_application.IsRunning) await RunBusyAsync(() => EditDatabaseGroupAsync(false)); };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _databaseState.Name = "DatabaseConfigurationState"; _databaseState.Dock = DockStyle.Fill; _databaseState.Margin = new Padding(8);
        content.Controls.Add(_databaseState, 0, 0); content.Controls.Add(_databaseTabs, 0, 1);
        content.SizeChanged += (_, _) => SetWrappingWidth(_databaseState, content.ClientSize.Width - 16);
        return UiTheme.PageLayout("数据库", "配置 MySQL、PostgreSQL 或 Oracle；标准点行可直接查询历史，自定义映射写入已有业务表。", UiTheme.Toolbar(), content);
    }

    private Control DatabasePanel(FlowLayoutPanel toolbar, DataGridView grid)
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(toolbar, 0, 0); content.Controls.Add(grid, 0, 1);
        return ScrollableContent(content, grid.Name + "Viewport", () => toolbar.Height + UiTheme.Scale(content, 170));
    }

    private void BindDatabaseViews(Guid? connectionId = null, Guid? groupId = null)
    {
        if (_databaseGrid.Columns.Count == 0) return;
        var statuses = _application.GetDatabaseStatuses().ToDictionary(status => status.ConnectionId);
        var rows = _configuration.Databases.Select(connection =>
        {
            statuses.TryGetValue(connection.Id, out var status);
            var state = !connection.Enabled ? "未启用" : !_application.IsRunning ? "等待设备启动" : status?.Error is not null ? "写入异常" : status?.IsConnected == true ? "已连接" : "等待写入";
            return new DatabaseConnectionRow(connection.Id, connection.Name, DatabaseUiText.Provider(connection.Provider),
                $"{connection.Host}:{connection.Port} / {connection.Database}", state, status?.QueuedRows ?? 0, status?.WrittenRows ?? 0,
                $"{status?.DroppedRows ?? 0} / {status?.SkippedRows ?? 0}", status?.Error ?? "—",
                $"{(status?.QueuedBytes ?? 0) / 1048576d:F2} MiB / {status?.PeakQueuedRows ?? 0:N0} 行" + (status?.IsBackpressured == true ? "（高水位）" : ""),
                $"{status?.FailedBatches ?? 0} / {status?.RetriedBatches ?? 0} / {status?.TimedOutOperations ?? 0}",
                $"{status?.InFlightBatches ?? 0} 批 / {status?.LastWriteMilliseconds ?? 0:F1} ms");
        }).ToArray();
        GridViewState.Rebind(_databaseGrid, rows, row => row.Id, connectionId);
        var groups = _configuration.DatabaseWriteGroups.Select(group => new DatabaseGroupRow(group.Id, group.Name,
            _configuration.Databases.FirstOrDefault(connection => connection.Id == group.ConnectionId)?.Name ?? "连接不存在",
            DatabaseUiText.Mode(group.Mode), new DatabaseTableInfo(group.Schema, group.Table).ToString(),
            group.Mode == DatabaseStorageMode.PointRows ? group.TagIds.Count == 0 ? "全部启用点位" : $"{group.TagIds.Count} 个点位" : $"{group.Columns.Count} 个字段",
            group.Enabled ? "已启用" : "未启用")).ToArray();
        GridViewState.Rebind(_databaseGroupGrid, groups, row => row.Id, groupId);
        _databaseState.Text = _application.IsRunning ? "设备正在运行。停止全部设备后可修改数据库与写入组；当前已保存的写入配置继续工作。"
            : _configuration.Databases.Count == 0 ? "默认采集结果写入日志。添加数据库连接 → 配置写入组 → 启用连接与写入组 → 启动设备。"
            : "数据库配置已保存。标准点行记录每个采样结果；自定义字段映射按完整快照写入。连接与写入组均需启用。";
        foreach (DataGridViewRow row in _databaseGrid.Rows) if (row.DataBoundItem is DatabaseConnectionRow data) row.Cells["Error"].ToolTipText = data.Error;
    }

    private string? DatabasePointReferenceWarning(IEnumerable<TagConfiguration> points)
    {
        var names = points.ToDictionary(point => point.Id, point => point.Name);
        var references = new List<string>();
        foreach (var group in _configuration.DatabaseWriteGroups)
        {
            references.AddRange(group.TagIds.Where(names.ContainsKey).Select(id => $"写入组“{group.Name}”的点位选择：{names[id]}"));
            references.AddRange(group.Columns.Where(column => DatabaseUiText.NeedsPoint(column.Source) && column.TagId is Guid id && names.ContainsKey(id))
                .Select(column => $"写入组“{group.Name}”的字段 {column.Column}：{names[column.TagId!.Value]}"));
        }
        if (references.Count == 0) return null;
        return "这些点位仍被数据库写入组引用，当前配置未删除：\n\n" + string.Join("\n", references.Take(12))
            + (references.Count > 12 ? $"\n另有 {references.Count - 12} 处引用。" : "")
            + "\n\n请先停止全部设备，在“数据库 → 采集写入组”中取消点位选择或修改字段绑定并保存，再删除设备或点位。停用写入组不会解除引用。";
    }

    private Guid SelectedDatabaseId() => (_databaseGrid.CurrentRow?.DataBoundItem as DatabaseConnectionRow)?.Id
        ?? throw new InvalidOperationException("请先选择数据库连接。");
    private Guid SelectedDatabaseGroupId() => (_databaseGroupGrid.CurrentRow?.DataBoundItem as DatabaseGroupRow)?.Id
        ?? throw new InvalidOperationException("请先选择写入组。");
    private void RequireDatabaseStopped() { if (_application.IsRunning) throw new InvalidOperationException("请先停止全部设备，再修改数据库配置。"); }

    private Task EditDatabaseConnectionAsync(bool isNew)
    {
        RequireDatabaseStopped();
        var source = isNew ? new DatabaseConnectionConfiguration() : _configuration.Databases.Single(connection => connection.Id == SelectedDatabaseId());
        using var editor = new DatabaseConnectionDialog(source, _application);
        editor.SetApplyHandler(async () =>
        {
            var current = _application.GetConfiguration(); var index = current.Databases.FindIndex(connection => connection.Id == source.Id);
            if (isNew) current.Databases.Add(editor.Value);
            else if (index < 0) throw new InvalidOperationException("连接已不存在，请关闭后刷新列表。");
            else current.Databases[index] = editor.Value;
            await SaveDatabasesAsync(current, editor.Value.Id, null, $"数据库连接“{editor.Value.Name}”已保存。");
        }, false, exception => _log.Write(GatewayLogLevel.Warning, "数据库连接未保存：" + exception.Message));
        editor.ShowDialog(this); return Task.CompletedTask;
    }

    private Task EditDatabaseGroupAsync(bool isNew)
    {
        RequireDatabaseStopped();
        if (_configuration.Databases.Count == 0) throw new InvalidOperationException("请先在“数据库连接”页添加连接。");
        var source = isNew ? new DatabaseWriteGroupConfiguration { ConnectionId = _configuration.Databases[0].Id }
            : _configuration.DatabaseWriteGroups.Single(group => group.Id == SelectedDatabaseGroupId());
        using var editor = new DatabaseWriteGroupDialog(source, _configuration.Databases, _configuration.Devices, _application);
        editor.SetApplyHandler(async () =>
        {
            var current = _application.GetConfiguration(); var index = current.DatabaseWriteGroups.FindIndex(group => group.Id == source.Id);
            if (isNew) current.DatabaseWriteGroups.Add(editor.Value);
            else if (index < 0) throw new InvalidOperationException("写入组已不存在，请关闭后刷新列表。");
            else current.DatabaseWriteGroups[index] = editor.Value;
            await SaveDatabasesAsync(current, null, editor.Value.Id, $"写入组“{editor.Value.Name}”已保存。");
        }, false, exception => _log.Write(GatewayLogLevel.Warning, "数据库写入组未保存：" + exception.Message));
        editor.ShowDialog(this); return Task.CompletedTask;
    }

    private async Task SaveDatabasesAsync(GatewayConfiguration candidate, Guid? connectionId, Guid? groupId, string message)
    {
        var applied = await _application.ApplyDatabaseSettingsAsync(candidate.Databases, candidate.DatabaseWriteGroups);
        CompleteConfigurationCommit(applied, successMessage: message); BindDatabaseViews(connectionId, groupId);
    }
    private async Task ToggleDatabaseConnectionAsync()
    {
        RequireDatabaseStopped(); var current = _application.GetConfiguration(); var connection = current.Databases.Single(item => item.Id == SelectedDatabaseId());
        connection.Enabled = !connection.Enabled;
        await SaveDatabasesAsync(current, connection.Id, null, $"数据库连接“{connection.Name}”已{(connection.Enabled ? "启用" : "停用")}。");
    }
    private async Task ToggleDatabaseGroupAsync()
    {
        RequireDatabaseStopped(); var current = _application.GetConfiguration(); var group = current.DatabaseWriteGroups.Single(item => item.Id == SelectedDatabaseGroupId());
        group.Enabled = !group.Enabled;
        await SaveDatabasesAsync(current, null, group.Id, $"写入组“{group.Name}”已{(group.Enabled ? "启用" : "停用")}。");
    }
    private async Task DeleteDatabaseConnectionAsync()
    {
        RequireDatabaseStopped(); var id = SelectedDatabaseId(); var current = _application.GetConfiguration(); var connection = current.Databases.Single(item => item.Id == id);
        if (current.DatabaseWriteGroups.Any(group => group.ConnectionId == id)) throw new InvalidOperationException("该连接仍有写入组，请先删除或迁移这些写入组。");
        if (MessageBox.Show(this, $"删除数据库连接“{connection.Name}”？外部数据库中的表和数据保留。", "删除数据库连接", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        current.Databases.Remove(connection); await SaveDatabasesAsync(current, null, null, "数据库连接已删除。");
    }
    private async Task DeleteDatabaseGroupAsync()
    {
        RequireDatabaseStopped(); var id = SelectedDatabaseGroupId(); var current = _application.GetConfiguration(); var group = current.DatabaseWriteGroups.Single(item => item.Id == id);
        if (MessageBox.Show(this, $"删除写入组“{group.Name}”？外部数据库中的表和数据保留。", "删除写入组", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        current.DatabaseWriteGroups.Remove(group); await SaveDatabasesAsync(current, null, null, "数据库写入组已删除。");
    }
    private async Task TestSelectedDatabaseAsync()
    {
        var connection = _configuration.Databases.Single(item => item.Id == SelectedDatabaseId());
        var result = await _application.TestDatabaseConnectionAsync(connection);
        SetFooter(connection.Name + "：" + result.Message);
        MessageBox.Show(this, result.Message + (result.ServerVersion is null ? "" : "\n服务器版本：" + result.ServerVersion), "数据库连接测试",
            MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
    private async Task PreviewSelectedDatabaseGroupAsync()
    {
        var current = _application.GetConfiguration();
        var group = current.DatabaseWriteGroups.Single(item => item.Id == SelectedDatabaseGroupId());
        var connection = current.Databases.Single(item => item.Id == group.ConnectionId);
        var preview = await _application.PreviewDatabaseWriteAsync(connection, group);
        using var dialog = new DatabasePreviewDialog(preview); dialog.ShowDialog(this);
    }
    private sealed record DatabaseConnectionRow(Guid Id, string Name, string Provider, string Target, string State, long Queued, long Written, string Dropped, string Error, string Load, string Retry, string Duration);
    private sealed record DatabaseGroupRow(Guid Id, string Name, string Connection, string Mode, string Table, string Selection, string Enabled);
}
