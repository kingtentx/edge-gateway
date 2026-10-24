using EdgeGateway.Application;
using EdgeGateway.Model;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private const string DeviceConfigurationColumnName = "DeviceConfiguration";
    private const string DeviceStartColumnName = "DeviceStart";
    private const string DeviceStopColumnName = "DeviceStop";
    private readonly DataGridView _deviceGrid = UiTheme.Grid();
    private readonly DataGridView _tagGrid = UiTheme.Grid();

    private readonly TextBox _tagSearch = new() { Name = "TagSearch", Width = 205, PlaceholderText = "搜索当前设备的点位 / 地址" };
    private readonly Label _deviceSummary = UiTheme.Label("", 10, UiTheme.Muted);
    private readonly Label _tagHeading = UiTheme.Label("请选择设备", 12, UiTheme.Ink, true);
    private readonly Label _tagSummary = UiTheme.Label("选择设备后查看和维护点位", 10, UiTheme.Muted);
    private readonly List<(Control Control, Func<bool> Available)> _selectionCommands = [];
    private Guid? _tagDeviceId;
    private CommandMenuButton _deviceMore = null!, _tagMore = null!, _channelMenu = null!;

    private Control BuildDevicesPage()
    {
        _deviceGrid.Name = "DeviceList"; _tagGrid.Name = "TagList";
        UiTheme.AddColumn(_deviceGrid, "Name", "设备名称", 150, 160);
        AddDeviceActionColumn(DeviceStartColumnName, "启动", true);
        AddDeviceActionColumn(DeviceStopColumnName, "停止", false);
        var configurationColumn = new DataGridViewLinkColumn
        {
            Name = DeviceConfigurationColumnName, HeaderText = "配置", Text = "配置",
            UseColumnTextForLinkValue = true, TrackVisitedState = false,
            LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = UiTheme.Blue,
            ActiveLinkColor = UiTheme.Blue, VisitedLinkColor = UiTheme.Blue,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells, MinimumWidth = 72,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
        };
        configurationColumn.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _deviceGrid.Columns.Add(configurationColumn);
        if (_deviceGrid is DpiDataGridView dpiGrid) dpiGrid.RegisterColumn(configurationColumn, 72);
        UiTheme.AddColumn(_deviceGrid, "State", "采集状态", 96, 96);
        UiTheme.AddColumn(_deviceGrid, "PointCount", "点位", 72, 72);
        UiTheme.AddColumn(_deviceGrid, "Driver", "采集驱动", 140);
        UiTheme.AddColumn(_deviceGrid, "Channel", "通信通道", 120);
        UiTheme.AddColumn(_deviceGrid, "Group", "分组", 100);
        UiTheme.AddColumn(_deviceGrid, "Enabled", "启用", 72, 72);
        UiTheme.AddColumn(_deviceGrid, "Cycle", "周期 (ms)", 96);
        UiTheme.AddColumn(_deviceGrid, "LastSuccess", "最近成功采集", 160, 160);
        UiTheme.AddColumn(_deviceGrid, "Failures", "连续失败", 100, 100);
        UiTheme.AddColumn(_deviceGrid, "Error", "采集诊断", 270, 270);
        _deviceGrid.SelectionChanged += (_, _) => { if (!_rebindingDevices) { BindTags(); UpdateSelectionCommands(); } };
        _deviceGrid.CellContentClick += async (_, e) => await HandleDeviceGridActionAsync(e);
        _deviceGrid.CellDoubleClick += async (_, e) => await HandleDeviceGridActionAsync(e, doubleClick: true);
        _deviceGrid.SizeChanged += (_, _) => ArrangeDeviceColumns();
        _deviceGrid.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F2) { e.Handled = true; await EditDeviceAsync(); }
            else if (e.KeyCode == Keys.Delete) { e.Handled = true; await DeleteDeviceAsync(); }
        };
        UiTheme.AddColumn(_tagGrid, "Name", "点位名称", 150, 150);
        UiTheme.AddColumn(_tagGrid, "Address", "地址", 110);
        UiTheme.AddColumn(_tagGrid, "Type", "数据类型", 105);
        UiTheme.AddColumn(_tagGrid, "Unit", "单位", 70);
        UiTheme.AddColumn(_tagGrid, "Access", "读写", 86);
        UiTheme.AddColumn(_tagGrid, "Enabled", "启用", 72);
        UiTheme.AddColumn(_tagGrid, "Upload", "上传", 72);
        UiTheme.AddColumn(_tagGrid, "Method", "读取方法", 180, 180);
        UiTheme.AddColumn(_tagGrid, "Area", "寄存区", 110);
        UiTheme.AddColumn(_tagGrid, "ByteOrder", "字节序", 85);
        UiTheme.AddColumn(_tagGrid, "Scaling", "工程值换算", 160, 160);
        UiTheme.AddColumn(_tagGrid, "Scan", "点位周期 (ms)", 125);
        UiTheme.AddColumn(_tagGrid, "Deadband", "上报死区", 105);
        _tagGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && _tagGrid.Rows[e.RowIndex].DataBoundItem is TagRow tag) await EditTagAsync(tag.Id);
        };
        _tagGrid.SelectionChanged += (_, _) => UpdateSelectionCommands();
        _tagGrid.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F2) { e.Handled = true; await EditTagAsync(); }
            else if (e.KeyCode == Keys.Delete) { e.Handled = true; await DeleteTagAsync(); }
        };
        var addDevice = ConfigurationButton("添加设备", async (_, _) => await AddDeviceAsync(), true);
        addDevice.Name = "AddDevice";
        var probeDevice = SelectionButton("连接诊断", async (_, _) => await RunBusyAsync(() => ProbeSelectionAsync(false)), () => SelectedDevice() is not null);
        _deviceMore = new CommandMenuButton("更多操作", BuildDeviceMenu, exception => ShowError(exception.Message)) { Name = "DeviceMoreActions" };
        _channelMenu = new CommandMenuButton("通道管理", BuildChannelMenu, exception => ShowError(exception.Message)) { Name = "ChannelActions" };
        _configurationCommands.AddRange([_deviceMore, _channelMenu]);
        _deviceSummary.Margin = new Padding(4, 9, 0, 0);
        var content = new TableLayoutPanel { Name = "DevicesContent", ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 57));
        var filters = BuildDeviceFilters();
        content.Controls.Add(filters, 0, 0); content.Controls.Add(_deviceGrid, 0, 1);

        _tagMore = new CommandMenuButton("点位操作", BuildTagMenu, exception => ShowError(exception.Message)) { Name = "TagMoreActions" };
        _configurationCommands.Add(_tagMore);
        UiTheme.StyleInput(_tagSearch);
        _tagSearch.TextChanged += (_, _) => BindTags();
        var tagHeader = new TableLayoutPanel
        {
            Name = "TagActions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = new Padding(0, 10, 0, 0)
        };
        tagHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) tagHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tagHeading.Dock = _tagSummary.Dock = DockStyle.Fill;
        _tagHeading.Margin = new Padding(0, 0, 0, 1); _tagSummary.Margin = new Padding(0, 0, 0, 3);
        tagHeader.Controls.Add(_tagHeading, 0, 0); tagHeader.Controls.Add(_tagSummary, 0, 1);
        var tagToolbar = UiTheme.Toolbar(
            SelectionButton("添加点位", async (_, _) => await AddTagAsync(), () => SelectedDevice() is not null, true),
            SelectionButton("编辑点位", async (_, _) => await EditTagAsync(), () => SelectedTag() is not null),
            SelectionButton("单点读取", async (_, _) => await RunBusyAsync(() => ProbeSelectionAsync(true)), () => SelectedTag() is not null),
            _tagMore, _tagSearch);
        _tagSearch.Margin = new Padding(0, 3, 0, 6);
        tagHeader.Controls.Add(tagToolbar, 0, 2);
        tagHeader.SizeChanged += (_, _) =>
        {
            SetWrappingWidth(_tagHeading, tagHeader.ClientSize.Width);
            SetWrappingWidth(_tagSummary, tagHeader.ClientSize.Width);
        };
        content.Controls.Add(tagHeader, 0, 2); content.Controls.Add(_tagGrid, 0, 3);
        var viewport = ScrollableContent(content, "DevicesViewport", () =>
            filters.GetPreferredSize(new Size(Math.Max(1, content.ClientSize.Width), 0)).Height +
            tagHeader.GetPreferredSize(new Size(Math.Max(1, content.ClientSize.Width), 0)).Height + UiTheme.Scale(content, 300));
        return UiTheme.PageLayout("设备与点位", "选择设备维护点位；每台设备独立启停，双击设备或点击配置进入同一窗口。",
            UiTheme.Toolbar(addDevice, probeDevice, _deviceMore, _channelMenu, _deviceSummary), viewport);
    }

    private Button SelectionButton(string text, EventHandler action, Func<bool> available, bool primary = false)
    {
        var button = ConfigurationButton(text, action, primary);
        _selectionCommands.Add((button, available));
        return button;
    }

    private void UpdateSelectionCommands()
    {
        foreach (var command in _selectionCommands) command.Control.Enabled = CanEdit() && command.Available();
    }

    private void ArrangeDeviceColumns()
    {
        if (!IsDpiLayoutReady || _deviceGrid.Columns.Count == 0) return;
        // Keep the target's name visible while reading details. Do not freeze an entire
        // action block when it would consume the small monitor's horizontal viewport.
        _deviceGrid.Columns["Name"].Frozen = _deviceGrid.ClientSize.Width >= UiTheme.Scale(_deviceGrid, 340);
        _deviceGrid.Columns["Name"].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _deviceGrid.Columns["Name"].Width = UiTheme.Scale(_deviceGrid, 150);
    }

    private IReadOnlyList<MenuCommand> BuildDeviceMenu()
    {
        var id = SelectedDevice()?.Id;
        bool Target() => CanEdit() && id.HasValue && _configuration.Devices.Any(d => d.Id == id);
        return [
            new("ConfigureDevice", "配置所选设备  F2", () => id.HasValue ? ConfigureDeviceAsync(id.Value) : Task.CompletedTask, Target, "请先选择设备"),
            new("CopyDevice", "复制所选设备", () => CopyDeviceAsync(id), Target, "请先选择设备"),
            new("ImportDevices", "追加导入设备…", () => RunBusyAsync(ImportDevicesAsync), CanEdit, SeparateBefore: true),
            new("ExportConfiguration", "导出网关配置…", () => RunBusyAsync(ExportConfigurationAsync), CanEdit),
            new("ReplaceConfiguration", "替换整份配置…", () => RunBusyAsync(ImportConfigurationAsync), () => CanEdit() && !_application.IsRunning,
                "请先停止全部设备；整份配置将替换当前设备及平台设置"),
            new("DeleteDevice", "删除所选设备…", () => DeleteDeviceAsync(id), Target, "请先选择设备", SeparateBefore: true, Dangerous: true)
        ];
    }

    private IReadOnlyList<MenuCommand> BuildChannelMenu()
    {
        var id = (_deviceChannelFilter.SelectedItem as ChannelChoice)?.Id;
        bool Stopped() => CanEdit() && !_application.IsRunning;
        bool Target() => Stopped() && id.HasValue && _configuration.Channels.Any(c => c.Id == id);
        return [
            new("AddChannel", "添加通信通道…", () => EditChannelAsync(true), Stopped, "请先停止全部设备再维护通道"),
            new("EditChannel", "编辑所选通道…", () => EditChannelAsync(false, id), Target, "请先停止全部设备，并在筛选中选择具体通道"),
            new("DeleteChannel", "删除所选通道…", () => DeleteChannelAsync(id), () => Target() && !_configuration.Devices.Any(d => d.ChannelId == id), "请选择未被设备使用的具体通道，并停止全部设备", SeparateBefore: true, Dangerous: true)
        ];
    }

    private IReadOnlyList<MenuCommand> BuildTagMenu()
    {
        var deviceId = SelectedDevice()?.Id; var tagId = SelectedTag()?.Id;
        bool Device() => CanEdit() && deviceId.HasValue && _configuration.Devices.Any(d => d.Id == deviceId);
        bool Tag() => Device() && tagId.HasValue && _configuration.Devices.Single(d => d.Id == deviceId).Tags.Any(t => t.Id == tagId);
        return [
            new("ImportTags", "导入当前设备点位…", () => ImportTagsAsync(deviceId), Device, "请先选择设备"),
            new("ExportTags", "导出当前设备点位…", () => RunBusyAsync(() => ExportTagsAsync(deviceId: deviceId)), Device, "请先选择设备"),
            new("TagTemplate", "下载点位模板…", () => RunBusyAsync(() => ExportTagsAsync(template: true)), CanEdit),
            new("ClearTagSearch", "清除点位筛选", () => { _tagSearch.Clear(); return Task.CompletedTask; }, () => CanEdit() && _tagSearch.TextLength > 0, SeparateBefore: true),
            new("DeleteTag", "删除所选点位…", () => DeleteTagAsync(deviceId, tagId), Tag, "请先选择点位", SeparateBefore: true, Dangerous: true)
        ];
    }

    private void AddDeviceActionColumn(string name, string text, bool start)
    {
        var column = new DataGridViewButtonColumn
        {
            CellTemplate = new DeviceActionButtonCell { StartsDevice = start },
            Name = name, HeaderText = text, Text = text, UseColumnTextForButtonValue = true,
            FlatStyle = FlatStyle.Flat, AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
            MinimumWidth = 72, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
        };
        column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _deviceGrid.Columns.Add(column);
        if (_deviceGrid is DpiDataGridView dpiGrid) dpiGrid.RegisterColumn(column, 72);
    }

    private Button ConfigurationButton(string text, EventHandler action, bool primary = false)
    {
        var button = UiTheme.Button(text, action, primary); _configurationCommands.Add(button); return button;
    }

    private bool CanEdit() => !_busy && !_closing;

    private void BindEditorApply(EditorDialog editor, Guid? deviceId, Func<Task> apply)
    {
        editor.SetApplyHandler(async () =>
        {
            if (!CanEdit()) throw new InvalidOperationException("当前操作尚未完成，请稍后重试。输入内容已保留。");
            _busy = true; UpdateCommands();
            try { await apply(); }
            finally { _busy = false; UpdateCommands(); }
        }, deviceId.HasValue && _application.IsDeviceRunning(deviceId.Value), exception =>
        {
            _log.Write(GatewayLogLevel.Error, "配置未保存，编辑内容已保留：" + exception.Message);
            SetFooter("配置未保存，弹窗中的输入内容已保留。");
        });
        editor.SuppressApplyErrorDialogForSmoke = _smokeTest;
    }

    private async Task HandleDeviceGridActionAsync(DataGridViewCellEventArgs e, bool doubleClick = false)
    {
        try { await DeviceGridActionAsync(e, doubleClick); }
        catch (Exception exception)
        {
            if (_smokeTest) throw;
            ShowError(exception.Message);
        }
    }

    private Task DeviceGridActionAsync(DataGridViewCellEventArgs e, bool doubleClick = false)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _deviceGrid.Rows.Count || e.ColumnIndex < 0
            || e.ColumnIndex >= _deviceGrid.Columns.Count) return Task.CompletedTask;
        // Resolve the event row's stable identity for both click paths; the current
        // selection must never redirect configuration to a different device.
        if (_deviceGrid.Rows[e.RowIndex].DataBoundItem is not DeviceRow row) return Task.CompletedTask;
        var columnName = _deviceGrid.Columns[e.ColumnIndex].Name;
        if (doubleClick)
        {
            // Action columns already handle their first click. Do not invoke them twice.
            if (columnName is DeviceConfigurationColumnName or DeviceStartColumnName or DeviceStopColumnName)
                return Task.CompletedTask;
            columnName = DeviceConfigurationColumnName;
        }
        return DeviceRowActionAsync(row.Id, columnName);
    }

    private async Task DeviceRowActionAsync(Guid deviceId, string columnName)
    {
        if (_busy || _closing) return;
        if (columnName == DeviceConfigurationColumnName)
        {
            await ConfigureDeviceAsync(deviceId);
            return;
        }
        if (columnName is not (DeviceStartColumnName or DeviceStopColumnName)) return;
        var device = _configuration.Devices.FirstOrDefault(x => x.Id == deviceId);
        if (device is null) return;
        var start = columnName == DeviceStartColumnName;
        if (start && !device.Enabled) { SetFooter("该设备未启用，请在“配置 → 采集设置”勾选“启用设备”，保存后启动。"); return; }
        if (start == _application.IsDeviceRunning(deviceId)) return;
        await RunBusyAsync(() => ExecuteDeviceActionAsync(deviceId, start));
    }

    private Task ConfigureDeviceAsync(Guid deviceId)
    {
        if (_busy || _closing) return Task.CompletedTask;
        var device = _configuration.Devices.FirstOrDefault(x => x.Id == deviceId);
        if (device is null) return Task.CompletedTask;
        try
        {
            using var dialog = new DeviceConfigurationDialog(device, _drivers, channels: _configuration.Channels);
            BindEditorApply(dialog, deviceId, async () =>
            {
                var value = dialog.Value ?? throw new InvalidOperationException("设备配置尚未通过检查。");
                var index = _configuration.Devices.FindIndex(x => x.Id == deviceId);
                if (index < 0) throw new InvalidOperationException("设备已不存在，请取消当前编辑。");
                if (value.Id != deviceId) throw new InvalidOperationException("设备配置不能更改设备唯一标识。");
                var applied = await _application.UpdateDeviceAsync(value);
                CompleteConfigurationCommit(applied, deviceId, $"设备“{value.Name}”的配置已自动保存。");
            });
            dialog.ShowDialog(this);
        }
        catch (Exception exception)
        {
            if (_smokeTest) throw;
            ShowError("打开或应用设备配置失败：" + exception.Message);
        }
        return Task.CompletedTask;
    }

    private void UpdateDeviceConfigurationCommands()
    {
        if (_deviceGrid.Columns[DeviceConfigurationColumnName] is not DataGridViewLinkColumn column) return;
        column.Text = "配置";
        var statuses = _application.GetDeviceStatuses().ToDictionary(x => x.DeviceId);
        foreach (DataGridViewRow row in _deviceGrid.Rows)
        {
            if (row.DataBoundItem is not DeviceRow data) continue;
            var device = _configuration.Devices.FirstOrDefault(x => x.Id == data.Id);
            if (device is null) continue;
            var running = _application.IsDeviceRunning(device.Id);
            var status = statuses.GetValueOrDefault(device.Id);
            row.Cells["State"].Value = !device.Enabled ? "未启用"
                : running ? StateText(status?.State ?? DeviceState.Connecting) : "已停止";
            row.Cells["LastSuccess"].Value = status?.LastSuccess?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—";
            row.Cells["Failures"].Value = status?.ConsecutiveFailures ?? 0;
            row.Cells["Error"].Value = status?.Error ?? (running ? "采集中" : "—");
            row.Cells["Error"].ToolTipText = status?.Error ?? "";
            if (row.Cells[DeviceStartColumnName] is DeviceActionButtonCell start)
            {
                start.ActionEnabled = !_busy && !_closing && device.Enabled && !running;
                start.ToolTipText = !device.Enabled ? "请先在“配置 → 采集设置”勾选“启用设备”。"
                    : running ? "该设备已启动。" : _dirty ? "使用已保存的平台设置启动；未应用的平台草稿不会生效。" : "仅启动本行设备的采集。";
            }
            if (row.Cells[DeviceStopColumnName] is DeviceActionButtonCell stop)
            {
                stop.ActionEnabled = !_busy && !_closing && running;
                stop.ToolTipText = running ? "仅停止本行设备，其他设备继续采集。" : "该设备已停止。";
            }
            if (row.Cells[DeviceConfigurationColumnName] is not DataGridViewLinkCell cell) continue;
            cell.LinkColor = _busy || _closing ? UiTheme.Muted : UiTheme.Blue;
            cell.ActiveLinkColor = cell.VisitedLinkColor = cell.LinkColor;
            cell.ToolTipText = running
                ? "配置此设备；保存变更后仅重新连接此设备，其他设备继续采集。"
                : "配置该行设备的启用状态、名称、分组、采集驱动及通信参数。";
        }
        _deviceGrid.Invalidate();
    }

    private DeviceConfiguration? SelectedDevice() => _deviceGrid.CurrentRow?.DataBoundItem is DeviceRow row
        ? _configuration.Devices.FirstOrDefault(x => x.Id == row.Id) : null;
    private TagConfiguration? SelectedTag() => _tagGrid.CurrentRow?.DataBoundItem is TagRow row
        ? SelectedDevice()?.Tags.FirstOrDefault(x => x.Id == row.Id) : null;

    private Task AddDeviceAsync()
    {
        if (!CanEdit()) return Task.CompletedTask;
        var device = new DeviceConfiguration { Name = $"设备 {_configuration.Devices.Count + 1}", Tags = [new TagConfiguration { Name = "采集点位", Address = "sine", DataType = TagDataType.Float64 }] };
        using var editor = new DeviceConfigurationDialog(device, _drivers, isNew: true, channels: _configuration.Channels);
        BindEditorApply(editor, null, async () =>
        {
            var value = editor.Value ?? throw new InvalidOperationException("设备配置尚未通过检查。");
            var applied = await _application.AddDeviceAsync(value);
            CompleteConfigurationCommit(applied, value.Id, "设备已添加并自动保存，可在设备行点击启动。");
        });
        editor.ShowDialog(this);
        return Task.CompletedTask;
    }

    private async Task EditDeviceAsync()
    {
        var device = SelectedDevice();
        if (device is null) { SetFooter("请先选择一个设备。"); return; }
        await ConfigureDeviceAsync(device.Id);
    }

    private async Task DeleteDeviceAsync(Guid? deviceId = null)
    {
        if (!CanEdit()) return;
        var device = deviceId.HasValue ? _configuration.Devices.FirstOrDefault(d => d.Id == deviceId) : SelectedDevice(); if (device is null) return;
        var runningNotice = _application.IsDeviceRunning(device.Id) ? "\n该设备正在运行，删除成功后只停止本设备，其他设备继续采集。" : "";
        if (MessageBox.Show(this, $"删除设备“{device.Name}”及其 {device.Tags.Count} 个配置点位？已有历史数据仍然保留。{runningNotice}", "删除设备", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunBusyAsync(async () => CompleteConfigurationCommit(await _application.DeleteDeviceAsync(device.Id),
            successMessage: $"设备“{device.Name}”已删除并自动保存。"));
    }

    private Task AddTagAsync()
    {
        if (!CanEdit()) return Task.CompletedTask;
        var device = SelectedDevice(); if (device is null) { SetFooter("请先添加或选择设备。"); return Task.CompletedTask; }
        var descriptor = _drivers.GetDescriptor(device.DriverId);
        using var editor = new TagEditor(TagEditor.CreateDefaultTag(descriptor, $"点位 {device.Tags.Count + 1}"), descriptor);
        BindEditorApply(editor, device.Id, async () =>
        {
            var applied = await _application.AddTagAsync(device.Id, editor.Value);
            _tagSearch.Clear();
            CompleteConfigurationCommit(applied, device.Id, $"点位“{editor.Value.Name}”已添加并自动保存。", selectTag: editor.Value.Id);
        });
        editor.ShowDialog(this);
        return Task.CompletedTask;
    }

    private Task EditTagAsync(Guid? tagId = null)
    {
        if (!CanEdit()) return Task.CompletedTask;
        var device = SelectedDevice(); var tag = tagId.HasValue ? device?.Tags.FirstOrDefault(x => x.Id == tagId) : SelectedTag();
        if (device is null || tag is null) return Task.CompletedTask;
        using var editor = new TagEditor(Clone(tag), _drivers.GetDescriptor(device.DriverId));
        BindEditorApply(editor, device.Id, async () =>
        {
            var applied = await _application.UpdateTagAsync(device.Id, editor.Value);
            CompleteConfigurationCommit(applied, device.Id, $"点位“{editor.Value.Name}”已更新并自动保存。", selectTag: tag.Id);
        });
        editor.ShowDialog(this);
        return Task.CompletedTask;
    }

    private async Task DeleteTagAsync(Guid? deviceId = null, Guid? tagId = null)
    {
        if (!CanEdit()) return;
        var device = deviceId.HasValue ? _configuration.Devices.FirstOrDefault(d => d.Id == deviceId) : SelectedDevice();
        var tag = tagId.HasValue ? device?.Tags.FirstOrDefault(t => t.Id == tagId) : SelectedTag();
        if (device is null || tag is null) return;
        var notice = _application.IsDeviceRunning(device.Id) ? "\n保存后仅重新连接该点位所属设备，其他设备继续采集。" : "";
        if (MessageBox.Show(this, $"删除点位“{tag.Name}”？{notice}", "删除点位", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunBusyAsync(async () => CompleteConfigurationCommit(await _application.DeleteTagAsync(device.Id, tag.Id),
            device.Id, $"点位“{tag.Name}”已删除并自动保存。"));
    }

    private void RebindConfigurationViews(Guid? selectDevice = null, Guid? selectTag = null)
    {
        RefreshDeviceFilters();
        BindDeviceRows(selectDevice);
        BindTags(selectTag); BindCloudFields(); BindDeviceChoices(); UpdateDeviceConfigurationCommands();
    }

    private void BindTags(Guid? selectTag = null)
    {
        var device = SelectedDevice();
        if (_tagDeviceId != device?.Id)
        {
            _tagDeviceId = device?.Id;
            if (_tagSearch.TextLength > 0) { _tagSearch.Clear(); return; }
        }
        var descriptor = device is null ? null : _drivers.GetDescriptor(device.DriverId);
        _tagGrid.Columns["Method"].Visible = descriptor?.Methods.Count > 0;
        _tagGrid.Columns["Area"].Visible = descriptor is not null && !descriptor.IsLegacy && descriptor.Id != "simulator";
        _tagGrid.Columns["ByteOrder"].Visible = descriptor is not null && descriptor.Id != "simulator";
        var search = _tagSearch.Text.Trim();
        var rows = device?.Tags.Where(t => search.Length == 0 || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || t.Address.Contains(search, StringComparison.OrdinalIgnoreCase) || (t.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(t => new TagRow(t.Id, t.Name, t.Address, t.ReadMethod, t.DataType.ToString(), AreaText(t.Area),
                descriptor?.IsLegacy == true ? t.Endian.ToString() : t.ByteOrder.ToString(),
                !string.IsNullOrWhiteSpace(t.Expression) ? t.Expression : TagEditor.IsNumericType(t.DataType) ? $"x × {t.Scale:G6} + {t.Offset:G6}" : "原样保存",
                t.Unit, t.Writable ? "读 / 写" : "只读", t.Enabled ? "是" : "否", t.UploadEnabled ? "是" : "否",
                t.ScanIntervalMs == 0 ? "继承设备" : t.ScanIntervalMs.ToString(), t.Deadband.ToString("G", System.Globalization.CultureInfo.InvariantCulture))).ToList() ?? [];
        GridViewState.Rebind(_tagGrid, rows, row => row.Id, selectTag);
        _tagHeading.Text = device is null ? "请选择设备" : device.Name + " · 点位管理";
        _tagSummary.Text = device is null ? "在上方选择设备，或先添加采集设备。"
            : $"{descriptor!.Name}  ·  显示 {rows.Count} / 共 {device.Tags.Count} 个点位"
            + (search.Length > 0 && rows.Count == 0 ? "  ·  未找到匹配点位，可在点位操作中清除筛选。" : "");
        _tagSearch.Enabled = device is not null;
        UpdateSelectionCommands();
    }

    private async Task ImportDevicesAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "网关配置 (*.json)|*.json", Title = "追加导入设备（保留当前网关与平台配置）", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var imported = await _application.ReadConfigurationFileAsync(dialog.FileName);
        if (imported.Devices is null) throw new InvalidDataException("配置文件缺少设备集合。");
        if (MessageBox.Show(this, $"追加导入 {imported.Devices.Count} 台设备、{imported.Devices.Sum(x => x?.Tags?.Count ?? 0)} 个点位？\n已有设备与平台配置保持不变，新增设备导入后不会自动启动。\n重复标识或串口冲突将整批拒绝。", "追加导入设备", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await AppendImportedDevicesAsync(imported);
    }

    private async Task AppendImportedDevicesAsync(GatewayConfiguration imported)
    {
        if (_closing) throw new InvalidOperationException("程序正在退出，不能导入设备。");
        if (imported.Devices is null || imported.Devices.Count == 0)
            throw new InvalidDataException("配置文件中没有可导入的设备。");

        var applied = await _application.AppendDevicesAsync(imported.Devices);
        CompleteConfigurationCommit(applied, imported.Devices[0].Id,
            $"已追加 {imported.Devices.Count} 台设备、{imported.Devices.Sum(x => x.Tags.Count)} 个点位，并自动保存。原有网关与平台设置保持不变。");
    }

    private async Task ImportConfigurationAsync()
    {
        if (_application.IsRunning) throw new InvalidOperationException("请先停止采集，再导入配置。");
        using var dialog = new OpenFileDialog { Filter = "网关配置 (*.json)|*.json", Title = "导入网关配置", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        // Reuse the same in-memory version migration as normal startup; importing never rewrites the source file.
        var imported = await _application.ReadConfigurationFileAsync(dialog.FileName);
        var errors = ConfigurationValidator.Validate(imported, _drivers);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        var draftNotice = _dirty ? "\n尚未应用的平台设置也将被替换。" : "";
        if (MessageBox.Show(this, $"将以文件中的整份配置替换并保存，包括 {imported.Devices.Count} 个设备、网关与平台参数。{draftNotice}\n确认导入？", "导入配置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await CommitConfigurationAsync(Clone(imported), successMessage: "整份配置已导入并自动保存。", replaceCloudDraft: true, replaceConfiguration: true);
    }

    private async Task ExportConfigurationAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "网关配置 (*.json)|*.json", FileName = "edge-gateway-config.json", Title = "导出当前配置", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await _application.ExportConfigurationAsync(dialog.FileName);
        SetFooter("配置已导出；文件只包含密码环境变量名称，不包含密码。");
    }

    private static string AreaText(ModbusArea area) => area switch { ModbusArea.Coil => "线圈 (01)", ModbusArea.DiscreteInput => "离散输入 (02)", ModbusArea.InputRegister => "输入寄存器 (04)", _ => "保持寄存器 (03)" };
    private sealed record DeviceRow(Guid Id, string Name, string Channel, string Group, string Driver, string Enabled, string State, int PointCount, int Cycle);
    private sealed record TagRow(Guid Id, string Name, string Address, string Method, string Type, string Area, string ByteOrder, string Scaling, string Unit, string Access, string Enabled, string Upload, string Scan, string Deadband);

    private sealed class DeviceActionButtonCell : DataGridViewButtonCell
    {
        public DeviceActionButtonCell() { }

        public bool ActionEnabled { get; set; }
        public bool StartsDevice { get; set; }

        protected override AccessibleObject CreateAccessibilityInstance() => new DeviceActionAccessibleObject(this);

        private sealed class DeviceActionAccessibleObject(DeviceActionButtonCell owner)
            : DataGridViewButtonCellAccessibleObject(owner)
        {
            public override AccessibleStates State => base.State | (owner.ActionEnabled ? AccessibleStates.None : AccessibleStates.Unavailable);
            public override string DefaultAction => owner.ActionEnabled ? base.DefaultAction : "";
            public override void DoDefaultAction()
            {
                if (owner.ActionEnabled) base.DoDefaultAction();
            }
        }

        public override object Clone()
        {
            var clone = (DeviceActionButtonCell)base.Clone();
            clone.ActionEnabled = ActionEnabled;
            clone.StartsDevice = StartsDevice;
            return clone;
        }

        protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds,
            int rowIndex, DataGridViewElementStates elementState, object? value, object? formattedValue,
            string? errorText, DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle,
            DataGridViewPaintParts paintParts)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, elementState, value, formattedValue,
                errorText, cellStyle, advancedBorderStyle,
                paintParts & ~(DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.ContentBackground | DataGridViewPaintParts.Focus));
            if ((paintParts & DataGridViewPaintParts.ContentForeground) == 0 || DataGridView is null) return;
            var inset = UiTheme.Scale(DataGridView, 5);
            var bounds = Rectangle.Inflate(cellBounds, -inset, -inset);
            if (bounds.Width <= 2 || bounds.Height <= 2) return;
            var background = !ActionEnabled ? Color.FromArgb(237, 241, 246) : StartsDevice ? UiTheme.Blue : Color.White;
            var foreground = !ActionEnabled ? Color.FromArgb(152, 163, 179) : StartsDevice ? Color.White : UiTheme.Red;
            var border = ActionEnabled && StartsDevice ? UiTheme.Blue : UiTheme.Border;
            var radius = Math.Min(UiTheme.Scale(DataGridView, 6), Math.Min(bounds.Width, bounds.Height) / 2);
            using var path = new GraphicsPath();
            var diameter = Math.Max(2, radius * 2);
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(background);
            using var outline = new Pen(border);
            graphics.FillPath(fill, path); graphics.DrawPath(outline, path);
            TextRenderer.DrawText(graphics, formattedValue?.ToString() ?? "", cellStyle.Font, bounds,
                foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (DataGridView.Focused && DataGridView.CurrentCellAddress == new Point(ColumnIndex, rowIndex))
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(bounds, -2, -2), foreground, background);
        }
    }
}
