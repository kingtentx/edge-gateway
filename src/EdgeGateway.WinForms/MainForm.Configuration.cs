using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private const string DeviceConfigurationColumnName = "DeviceConfiguration";
    private readonly DataGridView _deviceGrid = UiTheme.Grid();
    private readonly DataGridView _tagGrid = UiTheme.Grid();
    private readonly Dictionary<string, Control> _cloudFields = [];
    private bool _binding;

    private Control BuildDevicesPage()
    {
        UiTheme.AddColumn(_deviceGrid, "Name", "设备名称", 130, 150);
        UiTheme.AddColumn(_deviceGrid, "Group", "分组", 90);
        UiTheme.AddColumn(_deviceGrid, "Driver", "采集驱动", 110);
        UiTheme.AddColumn(_deviceGrid, "Enabled", "启用", 60, 60);
        UiTheme.AddColumn(_deviceGrid, "PointCount", "点位数", 70, 70);
        UiTheme.AddColumn(_deviceGrid, "Cycle", "周期 (ms)", 90);
        var configurationColumn = new DataGridViewLinkColumn
        {
            Name = DeviceConfigurationColumnName, HeaderText = "配置", Text = "配置",
            UseColumnTextForLinkValue = true, TrackVisitedState = false,
            LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = UiTheme.Blue,
            ActiveLinkColor = UiTheme.Blue, VisitedLinkColor = UiTheme.Blue,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells, MinimumWidth = 80,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter },
            HeaderCell = new DataGridViewColumnHeaderCell
            {
                Style = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
            }
        };
        _deviceGrid.Columns.Add(configurationColumn);
        if (_deviceGrid is DpiDataGridView dpiGrid) dpiGrid.RegisterColumn(configurationColumn, 80);
        _deviceGrid.SelectionChanged += (_, _) => BindTags();
        _deviceGrid.CellContentClick += DeviceConfigurationCellContentClick;
        _deviceGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0
                && _deviceGrid.Columns[e.ColumnIndex].Name != DeviceConfigurationColumnName) EditDevice();
        };
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
        _tagGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditTag(); };
        var addDevice = ConfigurationButton("添加设备", (_, _) => AddDevice(), true);
        var editDevice = ConfigurationButton("编辑设备", (_, _) => EditDevice());
        var deleteDevice = ConfigurationButton("删除设备", (_, _) => DeleteDevice());
        var import = ConfigurationButton("导入配置", async (_, _) => await RunBusyAsync(ImportConfigurationAsync));
        var export = UiTheme.Button("导出配置", async (_, _) => await RunBusyAsync(ExportConfigurationAsync));
        var content = new TableLayoutPanel { Name = "DevicesContent", ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 57));
        content.Controls.Add(_deviceGrid, 0, 0);
        var tagToolbar = UiTheme.Toolbar(UiTheme.Label("选中设备的点位", 11, UiTheme.Ink, true),
            ConfigurationButton("添加点位", (_, _) => AddTag()), ConfigurationButton("编辑点位", (_, _) => EditTag()),
            ConfigurationButton("删除点位", (_, _) => DeleteTag()));
        tagToolbar.Padding = new Padding(0, 15, 0, 10);
        tagToolbar.Name = "TagActions";
        content.Controls.Add(tagToolbar, 0, 1); content.Controls.Add(_tagGrid, 0, 2);
        var viewport = ScrollableContent(content, "DevicesViewport", () => tagToolbar.PreferredSize.Height + UiTheme.Scale(content, 320));
        return UiTheme.PageLayout("设备与点位", "配置分组、连接和采集点表；修改后保存生效，运行中仅允许查看。", UiTheme.Toolbar(addDevice, editDevice, deleteDevice, import, export), viewport);
    }

    private Button ConfigurationButton(string text, EventHandler action, bool primary = false)
    {
        var button = UiTheme.Button(text, action, primary); _configurationCommands.Add(button); return button;
    }

    private bool CanEdit() => !_runtime.IsRunning && !_busy && !_closing;

    private void DeviceConfigurationCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _deviceGrid.Rows.Count || e.ColumnIndex < 0
            || _deviceGrid.Columns[e.ColumnIndex].Name != DeviceConfigurationColumnName) return;
        // Resolve the clicked row's stable identity; keyboard selection or a previous
        // selection must never redirect this action to a different device.
        if (_deviceGrid.Rows[e.RowIndex].DataBoundItem is DeviceRow row) ConfigureDevice(row.Id);
    }

    private void ConfigureDevice(Guid deviceId)
    {
        if (_busy || _closing) return;
        var device = _configuration.Devices.FirstOrDefault(x => x.Id == deviceId);
        if (device is null) return;
        try
        {
            var readOnly = _runtime.IsRunning;
            using var dialog = new DeviceConfigurationDialog(device, _drivers.GetFactory(device.DriverId), readOnly);
            if (dialog.ShowDialog(this) != DialogResult.OK || readOnly || dialog.Value is null) return;
            if (!CanEdit()) { SetFooter("采集状态已变化，设备配置未应用。请停止采集后重新配置。"); return; }
            var index = _configuration.Devices.FindIndex(x => x.Id == deviceId);
            if (index < 0) return;
            if (dialog.Value.Id != device.Id || dialog.Value.DriverId != device.DriverId)
                throw new InvalidOperationException("设备配置不能更改设备标识或采集驱动。");
            _configuration.Devices[index] = dialog.Value;
            MarkDirty(); RebindConfigurationViews(deviceId);
            SetFooter($"设备“{device.Name}”的配置已更新，请点击“保存配置”保存后生效。");
        }
        catch (Exception exception)
        {
            if (_smokeTest) throw;
            ShowError("打开或应用设备配置失败：" + exception.Message);
        }
    }

    private void UpdateDeviceConfigurationCommands()
    {
        if (_deviceGrid.Columns[DeviceConfigurationColumnName] is not DataGridViewLinkColumn column) return;
        column.Text = _runtime.IsRunning ? "查看" : "配置";
        foreach (DataGridViewRow row in _deviceGrid.Rows)
        {
            if (row.Cells[DeviceConfigurationColumnName] is not DataGridViewLinkCell cell) continue;
            cell.LinkColor = _busy || _closing ? UiTheme.Muted : UiTheme.Blue;
            cell.ActiveLinkColor = cell.VisitedLinkColor = cell.LinkColor;
            cell.ToolTipText = _runtime.IsRunning
                ? "查看该设备当前驱动的配置；停止采集后可修改。"
                : "配置该行设备的采集周期、通信超时及当前驱动专用参数。";
        }
        _deviceGrid.InvalidateColumn(column.Index);
    }

    private DeviceConfiguration? SelectedDevice() => _deviceGrid.CurrentRow?.DataBoundItem is DeviceRow row
        ? _configuration.Devices.FirstOrDefault(x => x.Id == row.Id) : null;
    private TagConfiguration? SelectedTag() => _tagGrid.CurrentRow?.DataBoundItem is TagRow row
        ? SelectedDevice()?.Tags.FirstOrDefault(x => x.Id == row.Id) : null;

    private void AddDevice()
    {
        if (!CanEdit()) return;
        var device = new DeviceConfiguration { Name = $"设备 {_configuration.Devices.Count + 1}", Tags = [new TagConfiguration { Name = "采集点位", Address = "sine", DataType = TagDataType.Float64 }] };
        using var editor = new DeviceEditor(device, _drivers.Drivers, isNew: true);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _configuration.Devices.Add(editor.Value); MarkDirty(); RebindConfigurationViews(editor.Value.Id);
    }

    private void EditDevice()
    {
        if (!CanEdit()) return;
        var device = SelectedDevice();
        if (device is null) { SetFooter("请先选择一个设备。"); return; }
        using var editor = new DeviceEditor(Clone(device), _drivers.Drivers);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _configuration.Devices[_configuration.Devices.FindIndex(x => x.Id == device.Id)] = editor.Value;
        MarkDirty(); RebindConfigurationViews(device.Id);
    }

    private void DeleteDevice()
    {
        if (!CanEdit()) return;
        var device = SelectedDevice(); if (device is null) return;
        if (MessageBox.Show(this, $"删除设备“{device.Name}”及其 {device.Tags.Count} 个配置点位？已有历史数据仍然保留。", "删除设备", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _configuration.Devices.Remove(device); MarkDirty(); RebindConfigurationViews();
    }

    private void AddTag()
    {
        if (!CanEdit()) return;
        var device = SelectedDevice(); if (device is null) { SetFooter("请先添加或选择设备。"); return; }
        var descriptor = _drivers.GetFactory(device.DriverId).Descriptor;
        using var editor = new TagEditor(TagEditor.CreateDefaultTag(descriptor, $"点位 {device.Tags.Count + 1}"), descriptor);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        device.Tags.Add(editor.Value); MarkDirty(); RebindConfigurationViews(device.Id);
    }

    private void EditTag()
    {
        if (!CanEdit()) return;
        var device = SelectedDevice(); var tag = SelectedTag(); if (device is null || tag is null) return;
        using var editor = new TagEditor(Clone(tag), _drivers.GetFactory(device.DriverId).Descriptor);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        device.Tags[device.Tags.FindIndex(x => x.Id == tag.Id)] = editor.Value;
        MarkDirty(); RebindConfigurationViews(device.Id);
    }

    private void DeleteTag()
    {
        if (!CanEdit()) return;
        var device = SelectedDevice(); var tag = SelectedTag(); if (device is null || tag is null) return;
        if (MessageBox.Show(this, $"删除点位“{tag.Name}”？", "删除点位", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        device.Tags.Remove(tag); MarkDirty(); RebindConfigurationViews(device.Id);
    }

    private void RebindConfigurationViews(Guid? selectDevice = null)
    {
        var selected = selectDevice ?? SelectedDevice()?.Id;
        _deviceGrid.DataSource = _configuration.Devices.Select(d => new DeviceRow(d.Id, d.Name, d.Group, d.DriverId, d.Enabled ? "是" : "否", d.Tags.Count, d.PollIntervalMs)).ToList();
        if (selected.HasValue)
            foreach (DataGridViewRow row in _deviceGrid.Rows)
                if (row.DataBoundItem is DeviceRow data && data.Id == selected.Value) { row.Selected = true; _deviceGrid.CurrentCell = row.Cells[0]; break; }
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
            TagEditor.IsNumericType(t.DataType) ? $"x × {t.Scale:G6} + {t.Offset:G6}" : "原样保存",
            t.Unit, t.Writable ? "读 / 写" : "只读", t.Enabled ? "是" : "否")).ToList() ?? [];
    }

    private async Task ImportConfigurationAsync()
    {
        if (_runtime.IsRunning) throw new InvalidOperationException("请先停止采集，再导入配置。");
        using var dialog = new OpenFileDialog { Filter = "网关配置 (*.json)|*.json", Title = "导入网关配置", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(dialog.FileName).Length > 2 * 1024 * 1024) throw new InvalidOperationException("配置文件超过 2 MB，请检查文件内容。");
        // Reuse the same in-memory version migration as normal startup; importing never rewrites the source file.
        var imported = await new JsonConfigurationStore(dialog.FileName).LoadAsync();
        var errors = ConfigurationValidator.Validate(imported, _drivers);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        if (MessageBox.Show(this, $"将用文件中的 {imported.Devices.Count} 个设备替换当前配置草稿。确认导入？", "导入配置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _configuration = Clone(imported); MarkDirty(); RebindConfigurationViews();
        SetFooter("导入完成。检查配置后点击“保存配置”生效。");
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
        TextField("gatewayId", "网关标识", x => _configuration.GatewayId = x);
        TextField("name", "网关名称", x => _configuration.Name = x);
        CheckField("autoStart", "启动程序后自动采集", x => _configuration.AutoStart = x);
        NumberField("retention", "历史保留天数", 1, 3650, x => _configuration.HistoryRetentionDays = x);
        NumberField("maxHistory", "最大历史记录数", 100, 10_000_000, x => _configuration.MaxHistoryRows = x);
        NumberField("maxOutbox", "最大待发送消息数", 1, 1_000_000, x => _configuration.MaxOutboxMessages = x);
        Section("MQTT 平台连接");
        CheckField("mqttEnabled", "启用 MQTT 上报", x => _configuration.Mqtt.Enabled = x);
        TextField("host", "Broker 主机", x => _configuration.Mqtt.Host = x);
        NumberField("port", "Broker 端口", 1, 65535, x => _configuration.Mqtt.Port = x);
        CheckField("tls", "TLS 加密连接", x => _configuration.Mqtt.UseTls = x);
        TextField("clientId", "Client ID", x => _configuration.Mqtt.ClientId = x);
        TextField("username", "用户名", x => _configuration.Mqtt.Username = x);
        TextField("passwordEnv", "密码环境变量名称", x => _configuration.Mqtt.PasswordEnvironmentVariable = x);
        TextField("topic", "上报主题", x => _configuration.Mqtt.Topic = x);
        NumberField("publishInterval", "上报周期 (ms)", 100, 3_600_000, x => _configuration.PublishIntervalMs = x);
        NumberField("mqttTimeout", "连接 / 发送超时 (ms)", 500, 120_000, x => _configuration.Mqtt.TimeoutMs = x);
        Section("数据与凭据");
        var tip = UiTheme.Label("主题支持 {gatewayId}。密码从指定环境变量读取；在 Windows 设置该环境变量后，重启程序生效。TLS 使用系统信任链验证服务器证书。", 9, UiTheme.Muted);
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
        return UiTheme.PageLayout("平台与系统", "配置 MQTT 连接、上报节奏和本地存储策略，保存后在下次启动采集时应用。", UiTheme.Toolbar(open), scroll);
    }

    private void BindCloudFields()
    {
        if (_cloudFields.Count == 0) return;
        _binding = true;
        try
        {
            void Text(string key, string value) => ((TextBox)_cloudFields[key]).Text = value;
            void Number(string key, int value) { var field = (NumericUpDown)_cloudFields[key]; field.Value = Math.Clamp(value, (int)field.Minimum, (int)field.Maximum); }
            void Check(string key, bool value) => ((CheckBox)_cloudFields[key]).Checked = value;
            Text("gatewayId", _configuration.GatewayId); Text("name", _configuration.Name); Check("autoStart", _configuration.AutoStart);
            Number("retention", _configuration.HistoryRetentionDays); Number("maxHistory", _configuration.MaxHistoryRows); Number("maxOutbox", _configuration.MaxOutboxMessages);
            Check("mqttEnabled", _configuration.Mqtt.Enabled); Text("host", _configuration.Mqtt.Host); Number("port", _configuration.Mqtt.Port);
            Check("tls", _configuration.Mqtt.UseTls); Text("clientId", _configuration.Mqtt.ClientId); Text("username", _configuration.Mqtt.Username);
            Text("passwordEnv", _configuration.Mqtt.PasswordEnvironmentVariable); Text("topic", _configuration.Mqtt.Topic);
            Number("publishInterval", _configuration.PublishIntervalMs); Number("mqttTimeout", _configuration.Mqtt.TimeoutMs);
        }
        finally { _binding = false; }
    }

    private static string AreaText(ModbusArea area) => area switch { ModbusArea.Coil => "线圈 (01)", ModbusArea.DiscreteInput => "离散输入 (02)", ModbusArea.InputRegister => "输入寄存器 (04)", _ => "保持寄存器 (03)" };
    private sealed record DeviceRow(Guid Id, string Name, string Group, string Driver, string Enabled, int PointCount, int Cycle);
    private sealed record TagRow(Guid Id, string Name, string Address, string Method, string Type, string Area, string ByteOrder, string Scaling, string Unit, string Access, string Enabled);
}
