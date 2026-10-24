using EdgeGateway.Application;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal static class DatabaseUiText
{
    internal static string Provider(ExternalDatabaseProvider value) => value switch
    { ExternalDatabaseProvider.MySql => "MySQL", ExternalDatabaseProvider.PostgreSql => "PostgreSQL", _ => "Oracle" };
    internal static string Mode(DatabaseStorageMode value) => value == DatabaseStorageMode.PointRows ? "标准点行（每点一行）" : "自定义字段映射";
    internal static string Source(DatabaseValueSource value) => value switch
    {
        DatabaseValueSource.EngineeringValue => "工程值", DatabaseValueSource.RawValue => "原始值", DatabaseValueSource.NumericValue => "数值",
        DatabaseValueSource.SampleTime => "采样时间", DatabaseValueSource.Quality => "质量", DatabaseValueSource.DeviceId => "设备标识",
        DatabaseValueSource.DeviceName => "设备名称", DatabaseValueSource.TagId => "点位标识", DatabaseValueSource.TagName => "点位名称",
        DatabaseValueSource.Address => "点位地址", DatabaseValueSource.Unit => "单位", DatabaseValueSource.Error => "错误说明",
        DatabaseValueSource.RecordTime => "记录时间", DatabaseValueSource.EventId => "记录编号", _ => "常量"
    };
    internal static string FieldType(DatabaseFieldType value) => value switch
    { DatabaseFieldType.Auto => "自动", DatabaseFieldType.Text => "文本", DatabaseFieldType.Int64 => "整数", DatabaseFieldType.Decimal => "精确小数", DatabaseFieldType.Double => "浮点", DatabaseFieldType.Boolean => "布尔", _ => "时间" };
    internal static bool NeedsPoint(DatabaseValueSource source) => source is not (DatabaseValueSource.RecordTime or DatabaseValueSource.EventId or DatabaseValueSource.Constant);
    internal sealed record EnumItem<T>(T Value, string Text);
    internal sealed record PointItem(Guid Id, string Text) { public override string ToString() => Text; }
}

internal sealed class DatabaseConnectionDialog : EditorDialog
{
    private readonly DatabaseConnectionConfiguration _source;
    private readonly IGatewayApplication _application;
    private readonly TextBox _name, _host, _database, _username, _password;
    private readonly ComboBox _provider;
    private readonly CheckBox _enabled, _tls;
    private readonly NumericUpDown _port, _connectTimeout, _commandTimeout, _batch, _flush, _capacity, _capacityMb, _retryAttempts;
    private readonly Label _testResult = EditorLayout.WrappingLabel("连接测试使用当前输入，不保存配置。", color: UiTheme.Muted);
    private bool _testing;
    internal DatabaseConnectionConfiguration Value { get; private set; } = null!;
    internal IEnumerable<Control> SmokeInputs => new Control[] { _name, _enabled, _provider, _host, _port, _database, _username, _password, _tls, _connectTimeout, _commandTimeout, _batch, _flush, _capacity, _capacityMb, _retryAttempts };

    internal DatabaseConnectionDialog(DatabaseConnectionConfiguration configuration, IGatewayApplication application)
        : base("数据库连接", "配置采集数据的外部数据库；直接输入密码并随连接配置保存。保存前需停止全部设备。")
    {
        _source = GatewayJson.Clone(configuration); _application = application;
        var fields = AddTab("连接设置");
        _name = TextInput(_source.Name); _enabled = new CheckBox { Text = "启用该数据库写入", AutoSize = true, Checked = _source.Enabled };
        _provider = SelectInput(Enum.GetValues<ExternalDatabaseProvider>().Cast<object>().ToArray(), _source.Provider);
        _provider.FormattingEnabled = true; _provider.Format += (_, e) => { if (e.ListItem is ExternalDatabaseProvider value) e.Value = DatabaseUiText.Provider(value); };
        _host = TextInput(_source.Host); _port = NumberInput(_source.Port, 1, 65535); _database = TextInput(_source.Database);
        _username = TextInput(_source.Username); _password = TextInput(_source.Password);
        _password.UseSystemPasswordChar = true;
        _password.PlaceholderText = "直接输入密码；无密码时留空";
        _tls = new CheckBox { Text = "启用 TLS（验证服务器证书）", AutoSize = true, Checked = _source.UseTls };
        Section(fields, "连接信息");
        Add(fields, "DatabaseName", "连接名称", _name); Add(fields, "DatabaseEnabled", "数据写入", _enabled);
        Add(fields, "DatabaseProvider", "数据库类型", _provider); Add(fields, "DatabaseHost", "服务器", _host);
        Add(fields, "DatabasePort", "端口", _port); Add(fields, "DatabaseCatalog", "数据库 / Oracle 服务名", _database);
        Add(fields, "DatabaseUsername", "用户名", _username); Add(fields, "DatabasePassword", "密码", _password);
        Add(fields, "DatabaseTls", "连接加密", _tls);
        var test = UiTheme.Button("测试连接", async (_, _) => await TestAsync()); test.Name = "TestDatabaseConnection";
        Span(fields, test); _testResult.Name = "DatabaseConnectionResult"; Span(fields, _testResult);
        fields = AddTab("批次与超时");
        _connectTimeout = NumberInput(_source.ConnectionTimeoutSeconds, 1, 120); _commandTimeout = NumberInput(_source.CommandTimeoutSeconds, 1, 300);
        _batch = NumberInput(_source.BatchSize, 1, 5000); _flush = NumberInput(_source.FlushIntervalMs, 50, 60000); _capacity = NumberInput(_source.QueueCapacityRows, 1, 1000000);
        _capacityMb = NumberInput(_source.QueueCapacityMb, 1, 1024); _retryAttempts = NumberInput(_source.MaxRetryAttempts, 1, 10);
        Section(fields, "连接与命令"); Add(fields, "DatabaseConnectTimeout", "连接超时（秒）", _connectTimeout); Add(fields, "DatabaseCommandTimeout", "命令超时（秒）", _commandTimeout);
        Section(fields, "批量写入", "采集结果通过独立队列批量写入；队列积压、失败及丢弃数量在数据库页显示。");
        Add(fields, "DatabaseBatchSize", "每批最大行数", _batch); Add(fields, "DatabaseFlushInterval", "提交间隔（ms）", _flush); Add(fields, "DatabaseQueueCapacity", "队列最大行数", _capacity);
        Add(fields, "DatabaseQueueCapacityMb", "队列最大大小（MiB）", _capacityMb); Add(fields, "DatabaseMaxRetryAttempts", "每批最多尝试次数", _retryAttempts);
        Hint(fields, "同一连接下所有写入组共享行数和大小配额；队列满时拒绝新记录并计数。单行最多 1 MiB。");
        _provider.SelectedIndexChanged += (_, _) =>
        {
            if (_provider.SelectedItem is not ExternalDatabaseProvider selected) return;
            if (_port.Value is 3306 or 5432 or 1521) _port.Value = selected switch { ExternalDatabaseProvider.MySql => 3306, ExternalDatabaseProvider.PostgreSql => 5432, _ => 1521 };
        };
    }

    private DatabaseConnectionConfiguration CaptureConnectionDraft()
    {
        var value = GatewayJson.Clone(_source);
        value.Name = Required(_name, "连接名称"); value.Host = Required(_host, "服务器"); value.Database = Required(_database, "数据库 / 服务名");
        value.Username = Required(_username, "用户名"); value.Password = _password.Text;
        value.Provider = (ExternalDatabaseProvider)_provider.SelectedItem!; value.Port = (int)_port.Value; value.Enabled = _enabled.Checked; value.UseTls = _tls.Checked;
        value.ConnectionTimeoutSeconds = (int)_connectTimeout.Value; value.CommandTimeoutSeconds = (int)_commandTimeout.Value;
        value.BatchSize = (int)_batch.Value; value.FlushIntervalMs = (int)_flush.Value; value.QueueCapacityRows = (int)_capacity.Value; value.QueueCapacityMb = (int)_capacityMb.Value; value.MaxRetryAttempts = (int)_retryAttempts.Value;
        return value;
    }

    private async Task TestAsync()
    {
        if (_testing) return;
        try
        {
            var connection = CaptureConnectionDraft(); _testing = true; Tabs.Enabled = false; UseWaitCursor = true;
            _testResult.Text = "正在连接…"; _testResult.ForeColor = UiTheme.Muted;
            var result = await _application.TestDatabaseConnectionAsync(connection);
            _testResult.Text = result.Message + (string.IsNullOrWhiteSpace(result.ServerVersion) ? "" : "\n服务器版本：" + result.ServerVersion);
            _testResult.ForeColor = result.Success ? UiTheme.Green : UiTheme.Red;
        }
        catch (Exception exception) { _testResult.Text = exception.Message; _testResult.ForeColor = UiTheme.Red; }
        finally { _testing = false; Tabs.Enabled = true; UseWaitCursor = false; }
    }
    protected override void Commit() { if (_testing) throw new InvalidOperationException("连接测试尚未完成，请稍后保存。"); Value = CaptureConnectionDraft(); }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (_testing) e.Cancel = true; base.OnFormClosing(e); }
    internal DatabaseConnectionConfiguration CommitForSmoke() { Commit(); return Value; }
    internal static void Add(TableLayoutPanel fields, string name, string label, Control control) { control.Name = name; Field(fields, label, control); }
    internal static void Span(TableLayoutPanel fields, Control control)
    { var row = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize)); fields.Controls.Add(control, 0, row); fields.SetColumnSpan(control, 2); }
}

internal sealed class DatabaseWriteGroupDialog : EditorDialog
{
    private readonly DatabaseWriteGroupConfiguration _source;
    private readonly IGatewayApplication _application;
    private readonly IReadOnlyList<DatabaseConnectionConfiguration> _connections;
    private readonly IReadOnlyList<DatabaseUiText.PointItem> _points;
    private readonly TextBox _name, _schema, _table;
    private readonly ComboBox _connection, _mode, _tables;
    private readonly CheckBox _enabled, _allPoints, _goodOnly;
    private readonly NumericUpDown _interval, _maxAge;
    private readonly CheckedListBox _pointList = new() { Name = "DatabaseSelectedPoints", Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _pointSearch = new() { Name = "DatabasePointSearch", PlaceholderText = "搜索设备或点位", Dock = DockStyle.Fill };
    private readonly DataGridView _mapping = UiTheme.Grid();
    private readonly Label _status = EditorLayout.WrappingLabel("标准模式每个点位每次采样一行；自定义模式按字段映射写入已有表。", color: UiTheme.Muted);
    private readonly HashSet<Guid> _selectedPoints;
    private IReadOnlyList<DatabaseColumnInfo> _columns = [];
    private string? _metadataKey;
    private bool _loading, _bindingPoints, _bindingTable;
    private readonly TabPage _pointTab, _mappingTab;
    private readonly Button _loadColumns, _createTable;
    internal DatabaseWriteGroupConfiguration Value { get; private set; } = null!;
    internal DataGridView SmokeMapping => _mapping;
    internal CheckedListBox SmokePointList => _pointList;

    internal DatabaseWriteGroupDialog(DatabaseWriteGroupConfiguration configuration,
        IReadOnlyList<DatabaseConnectionConfiguration> connections, IReadOnlyList<DeviceConfiguration> devices, IGatewayApplication application)
        : base("数据库写入组", "标准点行保存每个采样结果；自定义字段映射使用已有业务表。保存前需停止全部设备。")
    {
        _source = GatewayJson.Clone(configuration); _connections = GatewayJson.Clone(connections); _application = application;
        _selectedPoints = _source.TagIds.ToHashSet();
        _points = devices.SelectMany(device => device.Tags.Select(tag => new DatabaseUiText.PointItem(tag.Id,
            device.Name + " / " + tag.Name + (!device.Enabled || !tag.Enabled ? "（未启用）" : "")))).ToArray();
        var fields = AddTab("写入设置");
        _name = TextInput(_source.Name); _enabled = new CheckBox { Text = "启用该写入组", AutoSize = true, Checked = _source.Enabled };
        _connection = SelectInput(_connections.Select(c => new ConnectionChoice(c.Id, c.Name + " · " + DatabaseUiText.Provider(c.Provider))).Cast<object>().ToArray(), null);
        for (var i = 0; i < _connection.Items.Count; i++) if (_connection.Items[i] is ConnectionChoice choice && choice.Id == _source.ConnectionId) _connection.SelectedIndex = i;
        _mode = SelectInput(Enum.GetValues<DatabaseStorageMode>().Cast<object>().ToArray(), _source.Mode); _mode.FormattingEnabled = true;
        _mode.Format += (_, e) => { if (e.ListItem is DatabaseStorageMode value) e.Value = DatabaseUiText.Mode(value); };
        _schema = TextInput(_source.Schema); _schema.PlaceholderText = "PostgreSQL 常用 public；Oracle 使用所属用户";
        _table = TextInput(_source.Table); _tables = SelectInput([], null); _tables.Name = "DatabaseExistingTables";
        _interval = NumberInput(_source.WriteIntervalMs, 100, 3600000); _maxAge = NumberInput(_source.MaxSampleAgeMs, 100, 86400000);
        _goodOnly = new CheckBox { Text = "仅在关联点位全部为 Good 时写入整行", AutoSize = true, Checked = _source.RequireGoodQuality };
        Section(fields, "写入目标");
        Add(fields, "DatabaseGroupName", "写入组名称", _name); Add(fields, "DatabaseGroupEnabled", "写入开关", _enabled);
        Add(fields, "DatabaseGroupConnection", "数据库连接", _connection); Add(fields, "DatabaseStorageMode", "数据格式", _mode);
        Add(fields, "DatabaseSchema", "Schema / 所属用户", _schema); Add(fields, "DatabaseTable", "表名", _table);
        var listTables = UiTheme.Button("读取数据表", async (_, _) => await InspectAsync(async () =>
        {
            var tables = await _application.GetDatabaseTablesAsync(CurrentConnection());
            _bindingTable = true;
            try { _tables.Items.Clear(); _tables.Items.AddRange(tables.Cast<object>().ToArray()); }
            finally { _bindingTable = false; }
            _status.Text = $"读取到 {tables.Count} 张表。可从列表选择目标表，或直接填写表名。";
        })); listTables.Name = "ReadDatabaseTables";
        Span(fields, listTables); Field(fields, "已有数据表", _tables);
        _loadColumns = UiTheme.Button("读取表字段", async (_, _) => await InspectAsync(async () =>
        {
            var columns = await _application.GetDatabaseColumnsAsync(CurrentConnection(), CurrentTable());
            LoadColumns(columns); _status.Text = $"已读取 {columns.Count} 个字段。请在“字段映射”页完成必填列；转换类型和实际采样值仍须满足目标列的类型与非空约束。";
        })); _loadColumns.Name = "ReadDatabaseColumns";
        _createTable = UiTheme.Button("创建标准点行表", async (_, _) => await CreateTableAsync()); _createTable.Name = "CreateDatabasePointTable";
        var preview = UiTheme.Button("检查与预览", async (_, _) =>
        {
            try
            {
                Commit(); var draft = GatewayJson.Clone(Value); var connection = CurrentConnection();
                await InspectAsync(async () =>
                {
                    var result = await _application.PreviewDatabaseWriteAsync(connection, draft);
                    using var dialog = new DatabasePreviewDialog(result); dialog.ShowDialog(this);
                });
            }
            catch (Exception exception) { _status.Text = exception.Message; }
        });
        preview.Name = "PreviewDatabaseWrite";
        Span(fields, UiTheme.Toolbar(_loadColumns, _createTable, preview)); _status.Name = "DatabaseGroupResult"; Span(fields, _status);
        Section(fields, "自定义整行策略", "只对自定义字段映射生效；标准点行会保留 Bad / Uncertain 质量以便追溯。");
        Add(fields, "DatabaseWriteInterval", "最小写入间隔（ms）", _interval); Add(fields, "DatabaseMaxSampleAge", "点位最大数据年龄（ms）", _maxAge);
        Add(fields, "DatabaseRequireGood", "写入质量要求", _goodOnly);

        _pointTab = new TabPage("标准点行 · 点位选择") { BackColor = Color.White };
        var selection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10), Margin = Padding.Empty };
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); selection.RowStyles.Add(new RowStyle(SizeType.AutoSize)); selection.RowStyles.Add(new RowStyle(SizeType.AutoSize)); selection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _allPoints = new CheckBox { Name = "DatabaseAllEnabledPoints", Text = "全部启用点位（后续新增点位也写入）", AutoSize = true, Checked = _source.TagIds.Count == 0, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        UiTheme.StyleInput(_pointSearch); _pointSearch.Margin = new Padding(0, 0, 0, 8);
        selection.Controls.Add(_allPoints, 0, 0); selection.Controls.Add(_pointSearch, 0, 1); selection.Controls.Add(_pointList, 0, 2); _pointTab.Controls.Add(selection); Tabs.TabPages.Add(_pointTab);
        _pointList.ItemCheck += (_, e) => { if (_bindingPoints) return; var id = ((DatabaseUiText.PointItem)_pointList.Items[e.Index]).Id; if (e.NewValue == CheckState.Checked) _selectedPoints.Add(id); else _selectedPoints.Remove(id); };
        _pointSearch.TextChanged += (_, _) => BindPoints(); _allPoints.CheckedChanged += (_, _) => _pointList.Enabled = !_allPoints.Checked;
        BindPoints(); _pointList.Enabled = !_allPoints.Checked;

        _mappingTab = new TabPage("自定义 · 字段映射") { BackColor = Color.White };
        ConfigureMappingGrid(); _mappingTab.Controls.Add(_mapping); Tabs.TabPages.Add(_mappingTab);
        BindMappingRows(_source.Columns, _source.Columns.Select(mapping => new DatabaseColumnInfo(mapping.Column, "尚未读取", true)).ToArray());
        _mode.SelectedIndexChanged += (_, _) => UpdateMode();
        _tables.SelectedIndexChanged += (_, _) => { if (!_bindingTable && _tables.SelectedItem is DatabaseTableInfo table) { _schema.Text = table.Schema; _table.Text = table.Name; } };
        _connection.SelectedIndexChanged += (_, _) => InvalidateMetadata(); _schema.TextChanged += (_, _) => InvalidateMetadata(); _table.TextChanged += (_, _) => InvalidateMetadata();
        UpdateMode();
    }

    private static void Add(TableLayoutPanel fields, string name, string label, Control input) => DatabaseConnectionDialog.Add(fields, name, label, input);
    private static void Span(TableLayoutPanel fields, Control input) => DatabaseConnectionDialog.Span(fields, input);
    private DatabaseConnectionConfiguration CurrentConnection() => _connection.SelectedItem is ConnectionChoice choice
        ? _connections.Single(connection => connection.Id == choice.Id) : throw new EditorValidationException("请先配置并选择数据库连接。", _connection);
    private DatabaseTableInfo CurrentTable() => new(_schema.Text.Trim(), Required(_table, "表名"));
    private string CurrentMetadataKey() => CurrentConnection().Id + "|" + _schema.Text.Trim() + "|" + _table.Text.Trim();
    private void InvalidateMetadata() { _metadataKey = null; _status.Text = "目标已变更。自定义映射保存前请重新读取该表字段。"; }
    private void UpdateMode()
    {
        var standard = _mode.SelectedItem is DatabaseStorageMode.PointRows;
        _pointTab.Text = standard ? "标准点行 · 点位选择" : "点位选择（标准模式）";
        _mappingTab.Text = standard ? "字段映射（自定义模式）" : "自定义 · 字段映射";
        _loadColumns.Enabled = !standard; _createTable.Enabled = standard;
        _interval.Enabled = _maxAge.Enabled = _goodOnly.Enabled = !standard;
        _pointTab.Enabled = standard; _mappingTab.Enabled = !standard;
    }
    private void BindPoints()
    {
        _bindingPoints = true;
        try
        {
            _pointList.Items.Clear(); var search = _pointSearch.Text.Trim();
            foreach (var point in _points.Where(point => search.Length == 0 || point.Text.Contains(search, StringComparison.OrdinalIgnoreCase)))
                _pointList.Items.Add(point, _selectedPoints.Contains(point.Id));
        }
        finally { _bindingPoints = false; }
    }

    private void ConfigureMappingGrid()
    {
        _mapping.Name = "DatabaseColumnMappings"; _mapping.ReadOnly = false; _mapping.AllowUserToAddRows = false;
        _mapping.EditMode = DataGridViewEditMode.EditOnEnter;
        _mapping.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "写入", Width = 60, MinimumWidth = 60 });
        UiTheme.AddColumn(_mapping, "Column", "表字段", 130, 130); _mapping.Columns["Column"].ReadOnly = true;
        UiTheme.AddColumn(_mapping, "DataType", "数据库类型 / 要求", 160, 160); _mapping.Columns["DataType"].ReadOnly = true;
        _mapping.Columns.Add(new DataGridViewComboBoxColumn { Name = "Source", HeaderText = "值来源", MinimumWidth = 130, FillWeight = 130,
            DataSource = Enum.GetValues<DatabaseValueSource>().Select(value => new DatabaseUiText.EnumItem<DatabaseValueSource>(value, DatabaseUiText.Source(value))).ToArray(), ValueMember = "Value", DisplayMember = "Text" });
        _mapping.Columns.Add(new DataGridViewComboBoxColumn { Name = "Point", HeaderText = "绑定点位", MinimumWidth = 220, FillWeight = 220,
            DataSource = new[] { new DatabaseUiText.PointItem(Guid.Empty, "不绑定点位") }.Concat(_points).ToArray(), ValueMember = "Id", DisplayMember = "Text" });
        _mapping.Columns.Add(new DataGridViewComboBoxColumn { Name = "FieldType", HeaderText = "转换类型", MinimumWidth = 110, FillWeight = 110,
            DataSource = Enum.GetValues<DatabaseFieldType>().Select(value => new DatabaseUiText.EnumItem<DatabaseFieldType>(value, DatabaseUiText.FieldType(value))).ToArray(), ValueMember = "Value", DisplayMember = "Text" });
        UiTheme.AddColumn(_mapping, "Constant", "常量值", 150, 150);
        _mapping.CurrentCellDirtyStateChanged += (_, _) => { if (_mapping.IsCurrentCellDirty) _mapping.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _mapping.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) UpdateMappingRow(_mapping.Rows[e.RowIndex]); };
    }

    private void BindMappingRows(IReadOnlyList<DatabaseColumnMapping> mappings, IReadOnlyList<DatabaseColumnInfo> columns)
    {
        _mapping.Rows.Clear();
        foreach (var column in columns)
        {
            var mapping = mappings.FirstOrDefault(item => string.Equals(item.Column, column.Name, StringComparison.OrdinalIgnoreCase));
            var required = !column.IsNullable && !column.HasDefault && !column.IsGenerated;
            var index = _mapping.Rows.Add(mapping is not null || required, column.Name,
                column.DataType + (column.IsGenerated ? " · 自动生成" : required ? " · 必填" : column.HasDefault ? " · 有默认值" : " · 可空"),
                mapping?.Source ?? DatabaseValueSource.EngineeringValue, mapping?.TagId ?? Guid.Empty, mapping?.FieldType ?? DatabaseFieldType.Auto, mapping?.ConstantValue ?? "");
            var row = _mapping.Rows[index]; row.Tag = column;
            if (column.IsGenerated) { row.Cells["Include"].Value = false; row.ReadOnly = true; row.DefaultCellStyle.ForeColor = UiTheme.Muted; }
            UpdateMappingRow(row);
        }
    }

    private void UpdateMappingRow(DataGridViewRow row)
    {
        if (row.Cells["Source"].Value is not DatabaseValueSource source) return;
        var generated = row.Tag is DatabaseColumnInfo { IsGenerated: true };
        row.Cells["Point"].ReadOnly = generated || !DatabaseUiText.NeedsPoint(source);
        row.Cells["Constant"].ReadOnly = generated || source != DatabaseValueSource.Constant;
        row.Cells["Point"].Style.BackColor = row.Cells["Point"].ReadOnly ? UiTheme.Background : Color.White;
        row.Cells["Constant"].Style.BackColor = row.Cells["Constant"].ReadOnly ? UiTheme.Background : Color.White;
    }

    private IReadOnlyList<DatabaseColumnMapping> CaptureMappings(bool validate)
    {
        _mapping.EndEdit(); var mappings = new List<DatabaseColumnMapping>();
        foreach (DataGridViewRow row in _mapping.Rows)
        {
            if (row.Cells["Include"].Value is not true) continue;
            var source = row.Cells["Source"].Value is DatabaseValueSource selected ? selected : DatabaseValueSource.EngineeringValue;
            var point = row.Cells["Point"].Value is Guid id && id != Guid.Empty ? id : (Guid?)null;
            if (validate && DatabaseUiText.NeedsPoint(source) && (point is null || !_points.Any(item => item.Id == point)))
                throw new EditorValidationException($"字段“{row.Cells["Column"].Value}”的值来源为{DatabaseUiText.Source(source)}，请绑定有效点位。", _mapping);
            mappings.Add(new() { Column = row.Cells["Column"].Value?.ToString() ?? "", Source = source,
                TagId = DatabaseUiText.NeedsPoint(source) ? point : null,
                FieldType = row.Cells["FieldType"].Value is DatabaseFieldType type ? type : DatabaseFieldType.Auto,
                ConstantValue = row.Cells["Constant"].Value?.ToString() ?? "" });
        }
        return mappings;
    }

    private void LoadColumns(IReadOnlyList<DatabaseColumnInfo> columns)
    {
        if (columns.Count == 0) throw new InvalidDataException("该表不存在、无可读字段或当前账号没有权限。");
        var mappings = CaptureMappings(false); _columns = columns; _metadataKey = CurrentMetadataKey();
        BindMappingRows(mappings, columns);
    }

    private async Task InspectAsync(Func<Task> action)
    {
        if (_loading) return;
        _loading = true; Tabs.Enabled = false; UseWaitCursor = true; _status.Text = "正在访问数据库…"; _status.ForeColor = UiTheme.Muted;
        try { await action(); _status.ForeColor = UiTheme.Green; }
        catch (Exception exception) { _status.Text = exception.Message; _status.ForeColor = UiTheme.Red; }
        finally { _loading = false; Tabs.Enabled = true; UseWaitCursor = false; UpdateMode(); }
    }

    private async Task CreateTableAsync()
    {
        try
        {
            var connection = CurrentConnection(); var table = CurrentTable();
            if (MessageBox.Show(this, $"在数据库“{connection.Name}”中创建标准点行表 {table}？\n此操作会执行建表。已存在的表不会自动替换或修改；请核对目标与账号权限。", "确认创建标准表", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            await InspectAsync(async () => { await _application.CreateDatabasePointTableAsync(connection, table); _status.Text = "标准点行表已准备就绪。保存并启用写入组后开始写入。"; });
        }
        catch (Exception exception) { _status.Text = exception.Message; _status.ForeColor = UiTheme.Red; }
    }

    protected override void Commit()
    {
        if (_loading) throw new InvalidOperationException("数据库操作尚未完成，请稍后保存。");
        var value = GatewayJson.Clone(_source); value.Name = Required(_name, "写入组名称"); value.Enabled = _enabled.Checked;
        value.ConnectionId = CurrentConnection().Id; value.Mode = (DatabaseStorageMode)_mode.SelectedItem!;
        var table = CurrentTable(); value.Schema = table.Schema; value.Table = table.Name;
        value.WriteIntervalMs = (int)_interval.Value; value.MaxSampleAgeMs = (int)_maxAge.Value; value.RequireGoodQuality = _goodOnly.Checked;
        value.TagIds = _allPoints.Checked ? [] : _selectedPoints.ToList();
        if (value.Mode == DatabaseStorageMode.PointRows)
        {
            if (!_allPoints.Checked && value.TagIds.Count == 0) throw new EditorValidationException("请选择至少一个点位，或勾选“全部启用点位”。", _allPoints);
            value.Columns = [];
        }
        else
        {
            if (_metadataKey != CurrentMetadataKey()) throw new EditorValidationException("保存自定义字段映射前，请读取当前目标表的字段并核对必填列。", _table);
            value.Columns = CaptureMappings(true).ToList(); value.TagIds = [];
            if (value.Columns.Count == 0) throw new EditorValidationException("请至少选择一个要写入的字段。", _mapping);
            var missing = _columns.Where(column => !column.IsNullable && !column.HasDefault && !column.IsGenerated
                && !value.Columns.Any(mapping => string.Equals(mapping.Column, column.Name, StringComparison.OrdinalIgnoreCase))).Select(column => column.Name).ToArray();
            if (missing.Length > 0) throw new EditorValidationException("以下必填列尚未映射：" + string.Join("、", missing), _mapping);
            if (!value.Columns.Any(mapping => DatabaseUiText.NeedsPoint(mapping.Source)))
                throw new EditorValidationException("自定义整行至少需要绑定一个采集点位；只有新采样到达才会触发写入。", _mapping);
        }
        Value = value;
    }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (_loading) e.Cancel = true; base.OnFormClosing(e); }
    internal DatabaseWriteGroupConfiguration CommitForSmoke() { Commit(); return Value; }
    internal void LoadColumnsForSmoke(IReadOnlyList<DatabaseColumnInfo> columns) => LoadColumns(columns);
    internal void SelectModeForSmoke(DatabaseStorageMode mode) => _mode.SelectedItem = mode;
    internal void ShowMappingForSmoke() => Tabs.SelectedTab = _mappingTab;
    private sealed record ConnectionChoice(Guid Id, string Name) { public override string ToString() => Name; }
}
