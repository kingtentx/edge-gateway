using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private TabControl _databaseTabs = null!;
    private GatewayTable _databaseGrid = null!, _databaseGroupGrid = null!;
    private DatabasePageView _databasePageView = null!;

    private Control BuildDatabasesPage()
    {
        _databasePageView = new DatabasePageView();
        _databaseTabs = _databasePageView.DatabaseTabs;
        _databaseGrid = _databasePageView.ConnectionsGrid;
        _databaseGroupGrid = _databasePageView.GroupsGrid;
        _databasePageView.copyGroup.Click += async (_, _) => await RunBusyAsync(() => EditDatabaseGroupAsync(true, copySelected: true));
        _configurationCommands.Add(_databasePageView.copyGroup);
        _databaseGrid.Name = "DatabaseConnections"; _databaseGroupGrid.Name = "DatabaseWriteGroups";

        _databasePageView.addConnection.Click += async (_, _) => await RunBusyAsync(() => EditDatabaseConnectionAsync(true));
        _configurationCommands.Add(_databasePageView.addConnection);
        _databasePageView.editConnection.Click += async (_, _) => await RunBusyAsync(() => EditDatabaseConnectionAsync(false));
        _databasePageView.toggleConnection.Click += async (_, _) => await RunBusyAsync(() => ToggleDatabaseConnectionAsync());
        _configurationCommands.Add(_databasePageView.toggleConnection);
        _databasePageView.deleteConnection.Click += async (_, _) => await RunBusyAsync(() => DeleteDatabaseConnectionAsync());
        _configurationCommands.Add(_databasePageView.deleteConnection);
        _databasePageView.testConnection.Click += async (_, _) => await RunBusyAsync(() => TestSelectedDatabaseAsync());
        _databasePageView.addGroup.Click += async (_, _) => await RunBusyAsync(() => EditDatabaseGroupAsync(true));
        _configurationCommands.Add(_databasePageView.addGroup);
        _databasePageView.editGroup.Click += async (_, _) => await RunBusyAsync(() => EditDatabaseGroupAsync(false));
        _databasePageView.toggleGroup.Click += async (_, _) => await RunBusyAsync(() => ToggleDatabaseGroupAsync());
        _configurationCommands.Add(_databasePageView.toggleGroup);
        _databasePageView.deleteGroup.Click += async (_, _) => await RunBusyAsync(() => DeleteDatabaseGroupAsync());
        _configurationCommands.Add(_databasePageView.deleteGroup);
        _databasePageView.previewGroup.Click += async (_, _) => await RunBusyAsync(() => PreviewSelectedDatabaseGroupAsync());
        _databaseGrid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await RunBusyAsync(() => EditDatabaseConnectionAsync(false)); };
        _databaseGroupGrid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await RunBusyAsync(() => EditDatabaseGroupAsync(false)); };
        _databaseGrid.SelectionChanged += (_, _) => UpdateDatabaseCommands();
        _databaseGroupGrid.SelectionChanged += (_, _) => UpdateDatabaseCommands();
        return _databasePageView;
    }

    private void UpdateDatabaseCommands()
    {
        if (_databasePageView is null) return;
        var running = _application.IsRunning;
        var selectedConnection = _databaseGrid.CurrentRow?.DataBoundItem as DatabaseConnectionRow;
        var selectedGroup = _databaseGroupGrid.CurrentRow?.DataBoundItem as DatabaseGroupRow;
        var connectionText = running && selectedConnection is not null && ConnectionHasActiveGroups(selectedConnection.Id) ? "查看连接" : "编辑连接";
        var groupText = running && selectedGroup is not null && GroupIsEnabled(selectedGroup.Id) ? "查看写入组" : "编辑写入组";
        if (_databasePageView.editConnection.Text != connectionText) _databasePageView.editConnection.Text = connectionText;
        if (_databasePageView.editGroup.Text != groupText) _databasePageView.editGroup.Text = groupText;
        _databasePageView.editConnection.Enabled = !_busy && !_closing && _databaseGrid.CurrentRow is not null;
        _databasePageView.copyGroup.Enabled = !_busy && !_closing && _databaseGroupGrid.CurrentRow is not null;
        _databasePageView.editGroup.Enabled = !_busy && !_closing && _databaseGroupGrid.CurrentRow is not null;
        _databasePageView.toggleGroup.Text = selectedGroup is not null && _configuration.DatabaseWriteGroups.Any(group => group.Id == selectedGroup.Id && group.Enabled) ? "暂停写入" : "恢复写入";
    }

    private bool GroupIsEnabled(Guid id) => _configuration.DatabaseWriteGroups.Any(group => group.Id == id && group.Enabled
        && _configuration.Databases.Any(connection => connection.Id == group.ConnectionId && connection.Enabled));
    private bool ConnectionHasActiveGroups(Guid id) => _configuration.DatabaseWriteGroups.Any(group => group.ConnectionId == id && GroupIsEnabled(group.Id));

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
                $"{(status?.QueuedBytes ?? 0) / 1048576d:F2} M / {status?.PeakQueuedRows ?? 0:F0} 行" + (status?.IsBackpressured == true ? "（高水位）" : ""),
                $"{status?.FailedBatches ?? 0} / {status?.RetriedBatches ?? 0} / {status?.TimedOutOperations ?? 0}",
                $"{status?.InFlightBatches ?? 0} 批 / {status?.LastWriteMilliseconds ?? 0:F1} ms");
        }).ToArray();
        GridViewState.UpdateRows(_databaseGrid, rows, row => row.Id, connectionId);
        var groupStates = _application.GetDatabaseGroupStatuses().ToDictionary(state => state.GroupId);
        var groups = _configuration.DatabaseWriteGroups.Select(group => new DatabaseGroupRow(group.Id, group.Name,
            _configuration.Databases.FirstOrDefault(connection => connection.Id == group.ConnectionId)?.Name ?? "连接不存在",
            DatabaseUiText.Mode(group.Mode), new DatabaseTableInfo(group.Schema, group.Table).ToString(),
            group.Selection.Mode != DatabasePointSelectionMode.Points ? DatabaseUiText.Selection(group, _configuration.Devices)
                : group.Mode == DatabaseStorageMode.ColumnMapping ? $"{group.Columns.Count} 个字段"
                : (group.TagIds.Count == 0 ? "全部启用点位" : $"{group.TagIds.Count} 个点位")
                    + (group.Mode == DatabaseStorageMode.PointMapping ? $" / {group.Columns.Count} 个映射字段" : ""),
            groupStates.TryGetValue(group.Id, out var state) && state.IsDraining ? "正在排空，设备继续采集" + (state.Error is null ? "" : " · " + state.Error)
                : !GroupIsEnabled(group.Id) ? "已暂停写入" : state?.Error is not null ? "写入异常：" + state.Error
                : state?.IsAccepting == true ? $"接收写入 · 成功 {state.WrittenRows} 行" : "等待设备采集")).ToArray();
        GridViewState.UpdateRows(_databaseGroupGrid, groups, row => row.Id, groupId);
        foreach (GatewayTableRow row in _databaseGrid.Rows) if (row.DataBoundItem is DatabaseConnectionRow data) row.Cells["Error"].ToolTipText = data.Error;
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
            + "\n\n请先暂停相关写入组并等待排空，在“数据库 → 采集写入组”中取消点位选择或修改字段绑定并保存，再删除设备或点位。停用写入组不会解除引用。";
    }

    private Guid SelectedDatabaseId() => (_databaseGrid.CurrentRow?.DataBoundItem as DatabaseConnectionRow)?.Id
        ?? throw new InvalidOperationException("请先选择数据库连接。");
    private Guid SelectedDatabaseGroupId() => (_databaseGroupGrid.CurrentRow?.DataBoundItem as DatabaseGroupRow)?.Id
        ?? throw new InvalidOperationException("请先选择写入组。");

    private Task EditDatabaseConnectionAsync(bool isNew)
    {
        var source = isNew ? new DatabaseConnectionConfiguration() : _configuration.Databases.Single(connection => connection.Id == SelectedDatabaseId());
        using var editor = new DatabaseConnectionDialog(source, _application);
        if (!isNew && _application.IsRunning && ConnectionHasActiveGroups(source.Id))
        {
            editor.InspectSavedConfiguration(); editor.ShowDialog(this); return Task.CompletedTask;
        }
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

    private Task EditDatabaseGroupAsync(bool isNew, bool copySelected = false)
    {
        var configuration = _application.GetConfiguration();
        if (configuration.Databases.Count == 0) throw new InvalidOperationException("请先在“数据库连接”页添加连接。");
        var source = isNew ? DatabaseUiText.NewWriteGroup(configuration.Databases[0].Id, configuration.DatabaseWriteGroups)
            : configuration.DatabaseWriteGroups.Single(group => group.Id == SelectedDatabaseGroupId());
        if (copySelected)
        {
            source = GatewayJson.Clone(configuration.DatabaseWriteGroups.Single(group => group.Id == SelectedDatabaseGroupId()));
            source.Id = Guid.NewGuid(); source.Enabled = false;
            source.Name = UniqueCatalogName(source.Name + " 副本", configuration.DatabaseWriteGroups.Select(group => group.Name));
        }
        using var editor = new DatabaseWriteGroupDialog(source, configuration.Databases, configuration.Devices, _application, configuration.DeviceGroups);
        if (copySelected) editor.ShowCopyGuidance();
        if (!isNew && _application.IsRunning && GroupIsEnabled(source.Id))
        {
            editor.InspectSavedConfiguration(); editor.ShowDialog(this); return Task.CompletedTask;
        }
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
        var current = _application.GetConfiguration(); var connection = current.Databases.Single(item => item.Id == SelectedDatabaseId());
        if (connection.Enabled && MessageBox.Show(this, "暂停此连接下所有写入组？设备继续采集，暂停期间的新采样不会写入该连接，也不会自动补齐。已接收的数据继续按原配置排空。", "暂停数据库写入", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        connection.Enabled = !connection.Enabled;
        await SaveDatabasesAsync(current, connection.Id, null, $"数据库连接“{connection.Name}”已{(connection.Enabled ? "启用" : "停用")}。");
    }
    private async Task ToggleDatabaseGroupAsync()
    {
        var current = _application.GetConfiguration(); var group = current.DatabaseWriteGroups.Single(item => item.Id == SelectedDatabaseGroupId());
        if (group.Enabled && MessageBox.Show(this, $"暂停写入组“{group.Name}”？设备和其他写入组继续工作；暂停期间的新采样不会写入此组，也不会自动补齐。已接收数据继续按原配置排空。", "暂停写入组", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        group.Enabled = !group.Enabled;
        await SaveDatabasesAsync(current, null, group.Id, $"写入组“{group.Name}”已{(group.Enabled ? "启用" : "停用")}。");
    }
    private async Task DeleteDatabaseConnectionAsync()
    {
        var id = SelectedDatabaseId(); var current = _application.GetConfiguration(); var connection = current.Databases.Single(item => item.Id == id);
        if (current.DatabaseWriteGroups.Any(group => group.ConnectionId == id)) throw new InvalidOperationException("该连接仍有写入组，请先删除或迁移这些写入组。");
        if (MessageBox.Show(this, $"删除数据库连接“{connection.Name}”？外部数据库中的表和数据保留。", "删除数据库连接", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        current.Databases.Remove(connection); await SaveDatabasesAsync(current, null, null, "数据库连接已删除。");
    }
    private async Task DeleteDatabaseGroupAsync()
    {
        var id = SelectedDatabaseGroupId(); var current = _application.GetConfiguration(); var group = current.DatabaseWriteGroups.Single(item => item.Id == id);
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
