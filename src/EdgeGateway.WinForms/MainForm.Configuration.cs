using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;
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
    private readonly Dictionary<string, Control> _cloudFields = [];
    private GatewayConfiguration? _cloudDraft;
    private bool _binding;

    private Control BuildDevicesPage()
    {
        UiTheme.AddColumn(_deviceGrid, "Name", "设备名称", 130, 150);
        UiTheme.AddColumn(_deviceGrid, "Channel", "通信通道", 110, 110);
        UiTheme.AddColumn(_deviceGrid, "Group", "分组", 90);
        UiTheme.AddColumn(_deviceGrid, "Driver", "采集驱动", 110);
        UiTheme.AddColumn(_deviceGrid, "Enabled", "启用", 60, 60);
        UiTheme.AddColumn(_deviceGrid, "State", "采集状态", 88, 88);
        UiTheme.AddColumn(_deviceGrid, "PointCount", "点位数", 70, 70);
        UiTheme.AddColumn(_deviceGrid, "Cycle", "周期 (ms)", 90);
        UiTheme.AddColumn(_deviceGrid, "LastSuccess", "最近成功采集", 150, 150);
        UiTheme.AddColumn(_deviceGrid, "Failures", "连续失败", 85, 85);
        UiTheme.AddColumn(_deviceGrid, "Error", "采集诊断", 260, 260);
        AddDeviceActionColumn(DeviceStartColumnName, "启动", true);
        AddDeviceActionColumn(DeviceStopColumnName, "停止", false);
        var configurationColumn = new DataGridViewLinkColumn
        {
            Name = DeviceConfigurationColumnName, HeaderText = "配置", Text = "配置",
            UseColumnTextForLinkValue = true, TrackVisitedState = false,
            LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = UiTheme.Blue,
            ActiveLinkColor = UiTheme.Blue, VisitedLinkColor = UiTheme.Blue,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells, MinimumWidth = 80,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
        };
        // Keep the header cell that owns HeaderText; replacing it resets the caption.
        configurationColumn.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _deviceGrid.Columns.Add(configurationColumn);
        if (_deviceGrid is DpiDataGridView dpiGrid) dpiGrid.RegisterColumn(configurationColumn, 80);
        _deviceGrid.SelectionChanged += (_, _) => BindTags();
        _deviceGrid.CellContentClick += async (_, e) => await HandleDeviceGridActionAsync(e);
        _deviceGrid.CellDoubleClick += async (_, e) => await HandleDeviceGridActionAsync(e, doubleClick: true);
        UiTheme.AddColumn(_tagGrid, "Name", "点位名称", 120, 130);
        UiTheme.AddColumn(_tagGrid, "Address", "地址", 95);
        UiTheme.AddColumn(_tagGrid, "Method", "读取方法", 160, 160);
        UiTheme.AddColumn(_tagGrid, "Type", "数据类型", 95);
        UiTheme.AddColumn(_tagGrid, "Area", "寄存区", 100);
        UiTheme.AddColumn(_tagGrid, "ByteOrder", "字节序", 70);
        UiTheme.AddColumn(_tagGrid, "Scaling", "工程值换算", 130, 140);
        UiTheme.AddColumn(_tagGrid, "Unit", "单位", 65);
        UiTheme.AddColumn(_tagGrid, "Access", "读写", 80);
        UiTheme.AddColumn(_tagGrid, "Enabled", "启用", 60);
        UiTheme.AddColumn(_tagGrid, "Upload", "上传", 60);
        UiTheme.AddColumn(_tagGrid, "Scan", "点位周期 (ms)", 110);
        UiTheme.AddColumn(_tagGrid, "Deadband", "上报死区", 95);
        _tagGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && _tagGrid.Rows[e.RowIndex].DataBoundItem is TagRow tag) await EditTagAsync(tag.Id);
        };
        var addDevice = ConfigurationButton("添加设备", async (_, _) => await AddDeviceAsync(), true);
        var editDevice = ConfigurationButton("编辑设备", async (_, _) => await EditDeviceAsync());
        var copyDevice = ConfigurationButton("复制设备", async (_, _) => await CopyDeviceAsync());
        var probeDevice = UiTheme.Button("连接诊断", async (_, _) => await RunBusyAsync(() => ProbeSelectionAsync(false)));
        var deleteDevice = ConfigurationButton("删除设备", async (_, _) => await DeleteDeviceAsync());
        var importDevices = ConfigurationButton("导入设备", async (_, _) => await RunBusyAsync(ImportDevicesAsync));
        var import = ConfigurationButton("导入配置", async (_, _) => await RunBusyAsync(ImportConfigurationAsync));
        _stoppedConfigurationCommands.Add(import);
        var export = UiTheme.Button("导出配置", async (_, _) => await RunBusyAsync(ExportConfigurationAsync));
        var content = new TableLayoutPanel { Name = "DevicesContent", ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 57));
        var filters = BuildDeviceFilters();
        content.Controls.Add(filters, 0, 0);
        content.Controls.Add(_deviceGrid, 0, 1);
        var tagToolbar = UiTheme.Toolbar(UiTheme.Label("选中设备的点位", 11, UiTheme.Ink, true),
            ConfigurationButton("添加点位", async (_, _) => await AddTagAsync()), ConfigurationButton("编辑点位", async (_, _) => await EditTagAsync()),
            ConfigurationButton("删除点位", async (_, _) => await DeleteTagAsync()),
            UiTheme.Button("单点读取", async (_, _) => await RunBusyAsync(() => ProbeSelectionAsync(true))),
            ConfigurationButton("导入点位", async (_, _) => await ImportTagsAsync()),
            UiTheme.Button("导出点位", async (_, _) => await RunBusyAsync(() => ExportTagsAsync())),
            UiTheme.Button("点位模板", async (_, _) => await RunBusyAsync(() => ExportTagsAsync(template: true))));
        tagToolbar.Padding = new Padding(0, 15, 0, 10);
        tagToolbar.Name = "TagActions";
        content.Controls.Add(tagToolbar, 0, 2); content.Controls.Add(_tagGrid, 0, 3);
        var viewport = ScrollableContent(content, "DevicesViewport", () =>
            filters.GetPreferredSize(new Size(Math.Max(1, content.ClientSize.Width), 0)).Height +
            tagToolbar.GetPreferredSize(new Size(Math.Max(1, content.ClientSize.Width), 0)).Height + UiTheme.Scale(content, 320));
        return UiTheme.PageLayout("设备与点位", "按通道、分组和关键字定位设备。配置变更只重连目标设备；其他设备继续采集。", UiTheme.Toolbar(addDevice, editDevice, copyDevice, deleteDevice, probeDevice, importDevices, import, export), viewport);
    }

    private void AddDeviceActionColumn(string name, string text, bool start)
    {
        var column = new DataGridViewButtonColumn
        {
            CellTemplate = new DeviceActionButtonCell { StartsDevice = start },
            Name = name, HeaderText = text, Text = text, UseColumnTextForButtonValue = true,
            FlatStyle = FlatStyle.Flat, AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
            MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
        };
        column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _deviceGrid.Columns.Add(column);
        if (_deviceGrid is DpiDataGridView dpiGrid) dpiGrid.RegisterColumn(column, 80);
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
        }, deviceId.HasValue && _runtime.IsDeviceRunning(deviceId.Value), exception =>
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
        if (start == _runtime.IsDeviceRunning(deviceId)) return;
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
                var candidate = Clone(_configuration);
                candidate.Devices[index] = Clone(value);
                await CommitConfigurationAsync(candidate, deviceId, $"设备“{value.Name}”的配置已自动保存。");
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
        var statuses = _runtime.GetDeviceStatuses().ToDictionary(x => x.DeviceId);
        foreach (DataGridViewRow row in _deviceGrid.Rows)
        {
            if (row.DataBoundItem is not DeviceRow data) continue;
            var device = _configuration.Devices.FirstOrDefault(x => x.Id == data.Id);
            if (device is null) continue;
            var running = _runtime.IsDeviceRunning(device.Id);
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
            var candidate = Clone(_configuration);
            candidate.Devices.Add(Clone(value));
            await CommitConfigurationAsync(candidate, value.Id, "设备已添加并自动保存，可在设备行点击启动。");
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

    private async Task DeleteDeviceAsync()
    {
        if (!CanEdit()) return;
        var device = SelectedDevice(); if (device is null) return;
        var runningNotice = _runtime.IsDeviceRunning(device.Id) ? "\n该设备正在运行，删除成功后只停止本设备，其他设备继续采集。" : "";
        if (MessageBox.Show(this, $"删除设备“{device.Name}”及其 {device.Tags.Count} 个配置点位？已有历史数据仍然保留。{runningNotice}", "删除设备", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var candidate = Clone(_configuration);
        candidate.Devices.RemoveAll(x => x.Id == device.Id);
        await RunBusyAsync(() => CommitConfigurationAsync(candidate, successMessage: "设备已删除并自动保存。"));
    }

    private Task AddTagAsync()
    {
        if (!CanEdit()) return Task.CompletedTask;
        var device = SelectedDevice(); if (device is null) { SetFooter("请先添加或选择设备。"); return Task.CompletedTask; }
        var descriptor = _drivers.GetFactory(device.DriverId).Descriptor;
        using var editor = new TagEditor(TagEditor.CreateDefaultTag(descriptor, $"点位 {device.Tags.Count + 1}"), descriptor);
        BindEditorApply(editor, device.Id, async () =>
        {
            var candidate = Clone(_configuration);
            candidate.Devices.Single(x => x.Id == device.Id).Tags.Add(Clone(editor.Value));
            await CommitConfigurationAsync(candidate, device.Id, "点位已添加并自动保存。");
        });
        editor.ShowDialog(this);
        return Task.CompletedTask;
    }

    private Task EditTagAsync(Guid? tagId = null)
    {
        if (!CanEdit()) return Task.CompletedTask;
        var device = SelectedDevice(); var tag = tagId.HasValue ? device?.Tags.FirstOrDefault(x => x.Id == tagId) : SelectedTag();
        if (device is null || tag is null) return Task.CompletedTask;
        using var editor = new TagEditor(Clone(tag), _drivers.GetFactory(device.DriverId).Descriptor);
        BindEditorApply(editor, device.Id, async () =>
        {
            var candidate = Clone(_configuration);
            var tags = candidate.Devices.Single(x => x.Id == device.Id).Tags;
            tags[tags.FindIndex(x => x.Id == tag.Id)] = Clone(editor.Value);
            await CommitConfigurationAsync(candidate, device.Id, "点位已更新并自动保存。");
        });
        editor.ShowDialog(this);
        return Task.CompletedTask;
    }

    private async Task DeleteTagAsync()
    {
        if (!CanEdit()) return;
        var device = SelectedDevice(); var tag = SelectedTag(); if (device is null || tag is null) return;
        var notice = _runtime.IsDeviceRunning(device.Id) ? "\n保存后仅重新连接该点位所属设备，其他设备继续采集。" : "";
        if (MessageBox.Show(this, $"删除点位“{tag.Name}”？{notice}", "删除点位", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var candidate = Clone(_configuration);
        candidate.Devices.Single(x => x.Id == device.Id).Tags.RemoveAll(x => x.Id == tag.Id);
        await RunBusyAsync(() => CommitConfigurationAsync(candidate, device.Id, "点位已删除并自动保存。"));
    }

    private void RebindConfigurationViews(Guid? selectDevice = null)
    {
        RefreshDeviceFilters();
        BindDeviceRows(selectDevice);
        BindTags(); BindCloudFields(); BindDeviceChoices(); UpdateDeviceConfigurationCommands();
    }

    private void BindTags()
    {
        var device = SelectedDevice();
        var descriptor = device is null ? null : _drivers.GetFactory(device.DriverId).Descriptor;
        _tagGrid.Columns["Method"].Visible = descriptor?.Methods.Count > 0;
        _tagGrid.Columns["Area"].Visible = descriptor is not null && !descriptor.IsLegacy && descriptor.Id != "simulator";
        _tagGrid.Columns["ByteOrder"].Visible = descriptor is not null && descriptor.Id != "simulator";
        _tagGrid.DataSource = device?.Tags.Select(t => new TagRow(t.Id, t.Name, t.Address, t.ReadMethod, t.DataType.ToString(), AreaText(t.Area),
            descriptor?.IsLegacy == true ? t.Endian.ToString() : t.ByteOrder.ToString(),
            !string.IsNullOrWhiteSpace(t.Expression) ? t.Expression : TagEditor.IsNumericType(t.DataType) ? $"x × {t.Scale:G6} + {t.Offset:G6}" : "原样保存",
            t.Unit, t.Writable ? "读 / 写" : "只读", t.Enabled ? "是" : "否", t.UploadEnabled ? "是" : "否",
            t.ScanIntervalMs == 0 ? "继承设备" : t.ScanIntervalMs.ToString(), t.Deadband.ToString("G", System.Globalization.CultureInfo.InvariantCulture))).ToList() ?? [];
    }

    private async Task ImportDevicesAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "网关配置 (*.json)|*.json", Title = "追加导入设备（保留当前网关与平台配置）", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var imported = await new JsonConfigurationStore(dialog.FileName).LoadAsync();
        if (imported.Devices is null) throw new InvalidDataException("配置文件缺少设备集合。");
        if (MessageBox.Show(this, $"追加导入 {imported.Devices.Count} 台设备、{imported.Devices.Sum(x => x?.Tags?.Count ?? 0)} 个点位？\n已有设备与平台配置保持不变，新增设备导入后不会自动启动。\n重复标识或串口冲突将整批拒绝。", "追加导入设备", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await AppendImportedDevicesAsync(imported);
    }

    private async Task AppendImportedDevicesAsync(GatewayConfiguration imported)
    {
        if (_closing) throw new InvalidOperationException("程序正在退出，不能导入设备。");
        if (imported.Devices is null || imported.Devices.Count == 0)
            throw new InvalidDataException("配置文件中没有可导入的设备。");

        // Build and validate the entire merged draft before changing any live UI state.
        // Only devices are imported; identities, platform settings and old devices survive unchanged.
        var candidate = Clone(_configuration);
        var devices = Clone(imported.Devices);
        candidate.Devices.AddRange(devices);
        var errors = ConfigurationValidator.Validate(candidate, _drivers);
        if (errors.Count > 0) throw new InvalidDataException("设备未导入，请检查重复标识、串口冲突或配置内容：\n" + string.Join("\n", errors));

        await CommitConfigurationAsync(candidate, devices[0].Id,
            $"已追加 {devices.Count} 台设备、{devices.Sum(x => x.Tags.Count)} 个点位，并自动保存。原有网关与平台设置保持不变。");
    }

    private async Task ImportConfigurationAsync()
    {
        if (_runtime.IsRunning) throw new InvalidOperationException("请先停止采集，再导入配置。");
        using var dialog = new OpenFileDialog { Filter = "网关配置 (*.json)|*.json", Title = "导入网关配置", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        // Reuse the same in-memory version migration as normal startup; importing never rewrites the source file.
        var imported = await new JsonConfigurationStore(dialog.FileName).LoadAsync();
        var errors = ConfigurationValidator.Validate(imported, _drivers);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        var draftNotice = _dirty ? "\n尚未应用的平台设置也将被替换。" : "";
        if (MessageBox.Show(this, $"将以文件中的整份配置替换并保存，包括 {imported.Devices.Count} 个设备、网关与平台参数。{draftNotice}\n确认导入？", "导入配置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await CommitConfigurationAsync(Clone(imported), successMessage: "整份配置已导入并自动保存。", replaceCloudDraft: true);
    }

    private async Task ExportConfigurationAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "网关配置 (*.json)|*.json", FileName = "edge-gateway-config.json", Title = "导出当前配置", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(_configuration, JsonConfigurationStore.SerializerOptions), new UTF8Encoding(false));
        SetFooter("配置已导出；文件只包含密码环境变量名称，不包含密码。");
    }

    private Control BuildCloudPage()
    {
        var scroll = new Panel { Name = "CloudFieldsViewport", Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(18) };
        var fields = new TableLayoutPanel { Name = "CloudFields", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(8), MaximumSize = new Size(1000, 0) };
        var captions = new List<Label>();
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Section(string text)
        {
            var label = UiTheme.Label(text, 12, UiTheme.Blue, true); label.Padding = new Padding(0, 14, 0, 8);
            var row = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.Controls.Add(label, 0, row); fields.SetColumnSpan(label, 2);
        }
        void TextField(string key, string title, Action<string> update)
        {
            var input = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8), MinimumSize = new Size(120, 30) };
            input.TextChanged += (_, _) => { if (!_binding) { update(input.Text); MarkDirty(); } };
            AddField(key, title, input);
        }
        void NumberField(string key, string title, int min, int max, Action<int> update)
        {
            var input = new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8), ThousandsSeparator = true };
            input.ValueChanged += (_, _) => { if (!_binding) { update((int)input.Value); MarkDirty(); } };
            AddField(key, title, input);
        }
        void CheckField(string key, string title, Action<bool> update)
        {
            var input = new CheckBox { Text = "启用", AutoSize = true, MinimumSize = new Size(90, 32), Margin = new Padding(0, 4, 0, 8) };
            input.CheckedChanged += (_, _) => { if (!_binding) { update(input.Checked); MarkDirty(); } };
            AddField(key, title, input);
        }
        void ChoiceField<T>(string key, string title, Action<T> update, Func<T, string>? display = null) where T : struct, Enum
        {
            var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8), FormattingEnabled = true };
            EditorLayout.ConfigureDropDown(input, 550, 320);
            input.Items.AddRange(Enum.GetValues<T>().Cast<object>().ToArray());
            if (display is not null) input.Format += (_, e) => { if (e.ListItem is T value) e.Value = display(value); };
            input.SelectedIndexChanged += (_, _) => { if (!_binding && input.SelectedItem is T value) { update(value); MarkDirty(); } };
            AddField(key, title, input);
        }
        void AddField(string key, string title, Control input)
        {
            var row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var caption = UiTheme.Label(title, 10, UiTheme.Muted); caption.Padding = new Padding(0, 4, 10, 0);
            caption.Dock = DockStyle.Fill; input.Name = "CloudField_" + key;
            captions.Add(caption);
            fields.Controls.Add(caption, 0, row); fields.Controls.Add(input, 1, row);
            _cloudFields.Add(key, input); _configurationCommands.Add(input);
        }
        Section("网关标识与运行");
        TextField("gatewayId", "网关标识", x => CloudDraft.GatewayId = x);
        TextField("name", "网关名称", x => CloudDraft.Name = x);
        CheckField("autoStart", "启动程序后自动采集", x => CloudDraft.AutoStart = x);
        NumberField("retention", "历史保留天数", 1, 3650, x => CloudDraft.HistoryRetentionDays = x);
        NumberField("maxHistory", "最大历史记录数", 100, 10_000_000, x => CloudDraft.MaxHistoryRows = x);
        NumberField("maxOutbox", "最大待发送消息数", 1, 1_000_000, x => CloudDraft.MaxOutboxMessages = x);
        Section("平台接入与 MQTT");
        CheckField("mqttEnabled", "启用 MQTT 上报", x => CloudDraft.Mqtt.Enabled = x);
        ChoiceField<MqttPlatform>("platform", "接入平台", x => CloudDraft.Mqtt.Platform = x);
        TextField("host", "Broker 主机", x => CloudDraft.Mqtt.Host = x);
        NumberField("port", "Broker 端口", 1, 65535, x => CloudDraft.Mqtt.Port = x);
        CheckField("tls", "TLS 加密连接", x => CloudDraft.Mqtt.UseTls = x);
        TextField("clientId", "Client ID", x => CloudDraft.Mqtt.ClientId = x);
        TextField("username", "用户名", x => CloudDraft.Mqtt.Username = x);
        TextField("usernameEnv", "用户名 / Token 环境变量", x => CloudDraft.Mqtt.UsernameEnvironmentVariable = x);
        TextField("passwordEnv", "密码环境变量名称", x => CloudDraft.Mqtt.PasswordEnvironmentVariable = x);
        TextField("topic", "通用 MQTT 上报主题", x => CloudDraft.Mqtt.Topic = x);
        NumberField("mqttTimeout", "连接 / 发送超时 (ms)", 500, 120_000, x => CloudDraft.Mqtt.TimeoutMs = x);
        TextField("deviceType", "平台设备类型", x => CloudDraft.Mqtt.PlatformDeviceType = x);
        TextField("typeKey", "ThingsCloud 类型标识", x => CloudDraft.Mqtt.ThingsCloudTypeKey = x);
        TextField("platformGatewayId", "ThingsPanel 网关编号", x => CloudDraft.Mqtt.PlatformGatewayId = x);
        Section("上报策略");
        ChoiceField<TelemetryReportMode>("reportMode", "上报模式", x => CloudDraft.ReportMode = x,
            x => x switch { TelemetryReportMode.OnChange => "仅变化上报", TelemetryReportMode.OnChangeWithHeartbeat => "变化上报 + 周期心跳", _ => "周期全量上报" });
        NumberField("publishInterval", "上报 / 变化检查周期 (ms)", 100, 3_600_000, x => CloudDraft.PublishIntervalMs = x);
        NumberField("heartbeatInterval", "心跳周期 (ms)", 100, 86_400_000, x => CloudDraft.HeartbeatIntervalMs = x);
        Section("远程读写 RPC");
        CheckField("rpcEnabled", "接收远程 RPC 请求", x => CloudDraft.Mqtt.RpcEnabled = x);
        CheckField("remoteWrite", "允许远程写入（默认关闭）", x => CloudDraft.Mqtt.AllowRemoteWrite = x);
        NumberField("rpcTimeout", "RPC 命令超时 (ms)", 500, 60_000, x => CloudDraft.Mqtt.RpcTimeoutMs = x);
        TextField("rpcRequest", "通用 RPC 请求主题", x => CloudDraft.Mqtt.RpcRequestTopic = x);
        TextField("rpcResponse", "通用 RPC 回复主题", x => CloudDraft.Mqtt.RpcResponseTopic = x);
        Section("数据与凭据");
        var tip = UiTheme.Label("通用主题支持 {gatewayId}；专用平台按协议生成主题与报文。Token 请填环境变量名称；用户名环境变量非空时优先读取。RPC 仅访问已运行、已启用的点位；远程写入还需点位允许写入。配置、表达式或设备重启后会重新发送首帧；死区在点位中设置，心跳按最新采样原时间上报。TLS 使用系统信任链验证服务器证书。", 9, UiTheme.Muted);
        tip.Dock = DockStyle.Fill;
        var tipRow = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(tip, 0, tipRow); fields.SetColumnSpan(tip, 2);
        var path = UiTheme.Label(_dataDirectory, 10, UiTheme.Muted); path.Dock = DockStyle.Fill;
        var pathRow = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(path, 0, pathRow); fields.SetColumnSpan(path, 2);
        scroll.Controls.Add(fields);
        bool arrangingFields = false;
        void ArrangeFields()
        {
            if (arrangingFields || !fields.IsHandleCreated || !IsDpiLayoutReady) return;
            arrangingFields = true;
            try
            {
                var available = Math.Max(1, fields.ClientSize.Width - fields.Padding.Horizontal);
                var captionWidth = Math.Clamp((int)(available * .38), UiTheme.Scale(fields, 110), UiTheme.Scale(fields, 220));
                if (fields.ColumnStyles[0].Width != captionWidth) fields.ColumnStyles[0].Width = captionWidth;
                foreach (var caption in captions) SetWrappingWidth(caption, captionWidth);
                SetWrappingWidth(tip, available); SetWrappingWidth(path, available);
            }
            finally { arrangingFields = false; }
        }
        fields.SizeChanged += (_, _) => ArrangeFields();
        fields.HandleCreated += (_, _) => ArrangeFields();
        fields.DpiChangedAfterParent += (_, _) => ArrangeFields();
        DpiLayoutCompleted += (_, _) => ArrangeFields();
        var open = UiTheme.Button("打开数据目录", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(_dataDirectory) { UseShellExecute = true }); }
            catch (Exception exception) { ShowError(exception.Message); }
        });
        var apply = ConfigurationButton("应用设置", async (_, _) => await RunBusyAsync(ApplyCloudSettingsAsync), true);
        apply.Name = "ApplyCloudSettings";
        _stoppedConfigurationCommands.Add(apply);
        var reset = ConfigurationButton("重置修改", (_, _) =>
        {
            if (!CanEdit()) return;
            ResetCloudDraft(); BindCloudFields(); UpdateCommands();
            SetFooter("平台设置已恢复为上次保存的内容。");
        });
        reset.Name = "ResetCloudSettings";
        return UiTheme.PageLayout("平台与系统", "可随时编辑草稿；停止全部设备后应用全局设置。未应用草稿不影响设备按已保存设置启动和采集。", UiTheme.Toolbar(apply, reset, open), scroll);
    }

    private GatewayConfiguration CloudDraft => _cloudDraft ??= Clone(_configuration);

    private void ResetCloudDraft()
    {
        _cloudDraft = null;
        _dirty = false;
    }

    private async Task ApplyCloudSettingsAsync()
    {
        if (_runtime.IsRunning || _closing) throw new InvalidOperationException("请先停止全部运行设备，再应用平台设置。");
        if (!_dirty || _cloudDraft is null) return;
        // Device changes may have been committed since this draft was opened.
        // Apply only global/platform settings to the latest saved device collection.
        var candidate = Clone(_configuration);
        candidate.GatewayId = _cloudDraft.GatewayId;
        candidate.Name = _cloudDraft.Name;
        candidate.AutoStart = _cloudDraft.AutoStart;
        candidate.HistoryRetentionDays = _cloudDraft.HistoryRetentionDays;
        candidate.MaxHistoryRows = _cloudDraft.MaxHistoryRows;
        candidate.MaxOutboxMessages = _cloudDraft.MaxOutboxMessages;
        candidate.PublishIntervalMs = _cloudDraft.PublishIntervalMs;
        candidate.ReportMode = _cloudDraft.ReportMode;
        candidate.HeartbeatIntervalMs = _cloudDraft.HeartbeatIntervalMs;
        candidate.Mqtt = Clone(_cloudDraft.Mqtt);
        await CommitConfigurationAsync(candidate, successMessage: "平台与系统设置已应用并自动保存。", replaceCloudDraft: true);
    }

    private void BindCloudFields()
    {
        if (_cloudFields.Count == 0 || _dirty) return;
        _binding = true;
        try
        {
            void Text(string key, string value) => ((TextBox)_cloudFields[key]).Text = value;
            void Number(string key, int value) { var field = (NumericUpDown)_cloudFields[key]; field.Value = Math.Clamp(value, (int)field.Minimum, (int)field.Maximum); }
            void Check(string key, bool value) => ((CheckBox)_cloudFields[key]).Checked = value;
            void Choice(string key, object value) => ((ComboBox)_cloudFields[key]).SelectedItem = value;
            Text("gatewayId", _configuration.GatewayId); Text("name", _configuration.Name); Check("autoStart", _configuration.AutoStart);
            Number("retention", _configuration.HistoryRetentionDays); Number("maxHistory", _configuration.MaxHistoryRows); Number("maxOutbox", _configuration.MaxOutboxMessages);
            Check("mqttEnabled", _configuration.Mqtt.Enabled); Text("host", _configuration.Mqtt.Host); Number("port", _configuration.Mqtt.Port);
            Check("tls", _configuration.Mqtt.UseTls); Text("clientId", _configuration.Mqtt.ClientId); Text("username", _configuration.Mqtt.Username);
            Text("passwordEnv", _configuration.Mqtt.PasswordEnvironmentVariable); Text("topic", _configuration.Mqtt.Topic);
            Number("publishInterval", _configuration.PublishIntervalMs); Number("mqttTimeout", _configuration.Mqtt.TimeoutMs);
            Choice("platform", _configuration.Mqtt.Platform); Choice("reportMode", _configuration.ReportMode);
            Number("heartbeatInterval", _configuration.HeartbeatIntervalMs);
            Text("usernameEnv", _configuration.Mqtt.UsernameEnvironmentVariable);
            Text("deviceType", _configuration.Mqtt.PlatformDeviceType); Text("typeKey", _configuration.Mqtt.ThingsCloudTypeKey);
            Text("platformGatewayId", _configuration.Mqtt.PlatformGatewayId);
            Check("rpcEnabled", _configuration.Mqtt.RpcEnabled); Check("remoteWrite", _configuration.Mqtt.AllowRemoteWrite);
            Number("rpcTimeout", _configuration.Mqtt.RpcTimeoutMs);
            Text("rpcRequest", _configuration.Mqtt.RpcRequestTopic); Text("rpcResponse", _configuration.Mqtt.RpcResponseTopic);
        }
        finally { _binding = false; }
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
