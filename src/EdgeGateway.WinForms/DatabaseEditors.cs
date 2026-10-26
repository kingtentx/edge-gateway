using EdgeGateway.Application;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal static class DatabaseUiText
{
    internal static DatabaseWriteGroupConfiguration NewWriteGroup(Guid connectionId, IReadOnlyList<DatabaseWriteGroupConfiguration> groups)
    {
        var draft = new DatabaseWriteGroupConfiguration { ConnectionId = connectionId };
        var names = groups.Select(group => group.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = draft.Name;
        for (var suffix = 2; names.Contains(draft.Name); suffix++) draft.Name = $"{baseName}（{suffix}）";
        return draft;
    }

    internal static string Provider(ExternalDatabaseProvider value) => value switch
    { ExternalDatabaseProvider.MySql => "MySQL", ExternalDatabaseProvider.PostgreSql => "PostgreSQL", _ => "Oracle" };
    internal static string Mode(DatabaseStorageMode value) => value switch
    {
        DatabaseStorageMode.PointRows => "标准点行（每点一行）",
        DatabaseStorageMode.PointMapping => "自定义逐点（每点一行）",
        _ => "自定义整行（固定点位组合）"
    };
    internal static string Source(DatabaseValueSource value) => value switch
    {
        DatabaseValueSource.EngineeringValue => "value_text [工程值]", DatabaseValueSource.RawValue => "raw_value [原始值]",
        DatabaseValueSource.NumericValue => "NumericValue [数值·派生属性]",
        DatabaseValueSource.SampleTime => "timestamp [采样时间]", DatabaseValueSource.Quality => "quality [质量]",
        DatabaseValueSource.DeviceId => "device_id [设备标识]", DatabaseValueSource.DeviceName => "device_name [设备名称]",
        DatabaseValueSource.TagId => "tag_id [点位标识]", DatabaseValueSource.TagName => "tag_name [点位名称]",
        DatabaseValueSource.Address => "address [点位地址]", DatabaseValueSource.Unit => "Unit [单位·采样属性]",
        DatabaseValueSource.Error => "Error [错误说明·采样属性]", DatabaseValueSource.RecordTime => "RecordTime [记录时间·生成项]",
        DatabaseValueSource.EventId => "event_id [记录编号]", DatabaseValueSource.Constant => "Constant [手动常量]",
        _ => "不写入 [默认值/NULL]"
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
        Add(fields, "DatabaseQueueCapacityMb", "队列最大大小（M）", _capacityMb); Add(fields, "DatabaseMaxRetryAttempts", "每批最多尝试次数", _retryAttempts);
        Hint(fields, "同一连接下所有写入组共享行数和大小配额；队列满时拒绝新记录并计数。单行最多 1 M。");
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
    private readonly Label _mappingStatus = EditorLayout.WrappingLabel("", color: UiTheme.Muted);
    private readonly Label _mappingHint = EditorLayout.WrappingLabel("", color: UiTheme.Muted);
    private readonly Label _mappingEmpty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.White };
    private readonly HashSet<Guid> _selectedPoints;
    private IReadOnlyList<DatabaseColumnInfo> _columns = [];
    private string? _metadataKey;
    private bool _loading, _bindingPoints, _bindingTable;
    private bool _bindingMappings, _updatingMapping;
    // UI-only option; omitted columns are never persisted as an invalid source enum.
    private const DatabaseValueSource OmittedSource = (DatabaseValueSource)(-1);
    private readonly Dictionary<string, DatabaseValueSource> _samplingSources = new(StringComparer.Ordinal);
    private readonly TabPage _pointTab, _mappingTab;
    private readonly Button _loadColumns, _createTable, _mappingLoadColumns, _switchPointMapping;
    internal DatabaseWriteGroupConfiguration Value { get; private set; } = null!;
    internal DataGridView SmokeMapping => _mapping;
    internal CheckedListBox SmokePointList => _pointList;

    internal DatabaseWriteGroupDialog(DatabaseWriteGroupConfiguration configuration,
        IReadOnlyList<DatabaseConnectionConfiguration> connections, IReadOnlyList<DeviceConfiguration> devices, IGatewayApplication application)
        : base("数据库写入组", "标准点行保存每个采样结果；自定义字段映射使用已有业务表。保存前需停止全部设备。")
    {
        ConfigureWindow(new Size(1024, 680), new Size(604, 481));
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
        _loadColumns = UiTheme.Button("读取表字段并映射", async (_, _) => await ReadColumnsAsync()); _loadColumns.Name = "ReadDatabaseColumns";
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
        Section(fields, "自定义写入策略", "逐点模式按每次采样生成行；最小写入间隔只用于固定点位组合。标准点行保留实际采样质量。");
        Add(fields, "DatabaseWriteInterval", "最小写入间隔（ms）", _interval); Add(fields, "DatabaseMaxSampleAge", "点位最大数据年龄（ms）", _maxAge);
        Add(fields, "DatabaseRequireGood", "写入质量要求", _goodOnly);

        _pointTab = new TabPage("标准点行 · 点位选择") { BackColor = Color.White };
        var selection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10), Margin = Padding.Empty };
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); selection.RowStyles.Add(new RowStyle(SizeType.AutoSize)); selection.RowStyles.Add(new RowStyle(SizeType.AutoSize)); selection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _allPoints = new CheckBox { Name = "DatabaseAllEnabledPoints", Text = "全部启用点位（后续新增点位也写入）", AutoSize = true, Checked = _source.TagIds.Count == 0, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        UiTheme.StyleInput(_pointSearch); _pointSearch.Margin = new Padding(0, 0, 0, 8);
        selection.Controls.Add(_allPoints, 0, 0); selection.Controls.Add(UiTheme.InputSurface(_pointSearch), 0, 1); selection.Controls.Add(_pointList, 0, 2); _pointTab.Controls.Add(selection); Tabs.TabPages.Add(_pointTab);
        _pointList.ItemCheck += (_, e) => { if (_bindingPoints) return; var id = ((DatabaseUiText.PointItem)_pointList.Items[e.Index]).Id; if (e.NewValue == CheckState.Checked) _selectedPoints.Add(id); else _selectedPoints.Remove(id); };
        _pointSearch.TextChanged += (_, _) => BindPoints(); _allPoints.CheckedChanged += (_, _) => _pointList.Enabled = !_allPoints.Checked;
        BindPoints(); _pointList.Enabled = !_allPoints.Checked;

        _mappingTab = new TabPage("自定义 · 字段映射") { BackColor = Color.White };
        ConfigureMappingGrid();
        _mappingLoadColumns = UiTheme.Button("读取 / 刷新字段", async (_, _) => await ReadColumnsAsync());
        _mappingLoadColumns.Name = "RefreshMappingColumns";
        _switchPointMapping = UiTheme.Button("使用自定义逐点映射", (_, _) => _mode.SelectedItem = DatabaseStorageMode.PointMapping, primary: true);
        _switchPointMapping.Name = "UsePointMapping";
        var mappingLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        mappingLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mappingLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); mappingLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mappingLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); mappingLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mappingLayout.Controls.Add(UiTheme.Toolbar(_switchPointMapping, _mappingLoadColumns), 0, 0);
        mappingLayout.Controls.Add(_mappingHint, 0, 1);
        var mappingBody = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        mappingBody.Controls.Add(_mapping); mappingBody.Controls.Add(_mappingEmpty);
        mappingLayout.Controls.Add(mappingBody, 0, 2); mappingLayout.Controls.Add(_mappingStatus, 0, 3);
        mappingLayout.SizeChanged += (_, _) =>
        {
            foreach (var label in new[] { _mappingHint, _mappingStatus })
                label.MaximumSize = new Size(Math.Max(1, mappingLayout.ClientSize.Width - label.Margin.Horizontal), 0);
        };
        _mappingStatus.Name = "DatabaseMappingResult"; _mappingEmpty.Name = "DatabaseMappingEmpty";
        _status.TextChanged += (_, _) => _mappingStatus.Text = _status.Text;
        _status.ForeColorChanged += (_, _) => _mappingStatus.ForeColor = _status.ForeColor;
        _mappingStatus.Text = _status.Text;
        _mappingTab.Controls.Add(mappingLayout); Tabs.TabPages.Add(_mappingTab);
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
    private void InvalidateMetadata()
    {
        _metadataKey = null;
        _status.Text = "目标已变更。映射草稿已保留；保存前请在“字段映射”页重新读取目标表字段。";
        _status.ForeColor = UiTheme.Muted;
    }
    private void UpdateMode()
    {
        var standard = _mode.SelectedItem is DatabaseStorageMode.PointRows;
        var pointMapping = _mode.SelectedItem is DatabaseStorageMode.PointMapping;
        _pointTab.Text = standard || pointMapping ? "点位选择" : "点位选择（逐点模式）";
        _mappingTab.Text = "字段映射";
        _loadColumns.Enabled = !standard; _createTable.Enabled = standard;
        _mappingLoadColumns.Enabled = !standard; _switchPointMapping.Visible = standard;
        _interval.Enabled = !standard && !pointMapping; _maxAge.Enabled = _goodOnly.Enabled = !standard;
        _goodOnly.Text = pointMapping ? "仅写入质量为 Good 的采样" : "仅在关联点位全部为 Good 时写入整行";
        _pointTab.Enabled = standard || pointMapping; _mapping.Enabled = !standard;
        _mapping.Columns["Point"].Visible = !pointMapping;
        _mappingHint.Text = standard ? "当前为标准点行模式，字段固定。已有业务表请切换为自定义逐点，再读取字段。"
            : pointMapping ? "勾选“使用采样”取当前点位值；取消勾选后填写手动常量，仍写入该字段。不需要的字段请选择“不写入”；自增列自动跳过。"
            : "勾选“使用采样”并绑定点位；取消勾选后填写手动常量，仍写入该字段。不需要的字段请选择“不写入”；自增列自动跳过。";
        _mappingEmpty.Visible = _mapping.Rows.Count == 0;
        _mappingEmpty.Text = standard ? "已有自定义表？点击上方“使用自定义逐点映射”。"
            : "尚未读取表字段。\n请先在“写入设置”选择数据库和表名，\n再点击上方“读取 / 刷新字段”。";
        if (_mappingEmpty.Visible) _mappingEmpty.BringToFront();
        foreach (DataGridViewRow row in _mapping.Rows) UpdateMappingRow(row);
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
        _mapping.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "使用采样", Width = 100, MinimumWidth = 100,
            ToolTipText = "勾选：使用采样来源。取消勾选：写入手动常量。若不写入此列，请在值来源中选择“不写入”。" });
        UiTheme.AddColumn(_mapping, "Column", "表字段", 130, 130); _mapping.Columns["Column"].ReadOnly = true;
        UiTheme.AddColumn(_mapping, "DataType", "数据库类型 / 要求", 160, 160); _mapping.Columns["DataType"].ReadOnly = true;
        _mapping.Columns.Add(new DataGridViewComboBoxColumn { Name = "Source", HeaderText = "值来源", MinimumWidth = 240, FillWeight = 240,
            DataSource = Enumerable.Append(Enum.GetValues<DatabaseValueSource>(), OmittedSource).Select(value => new DatabaseUiText.EnumItem<DatabaseValueSource>(value, DatabaseUiText.Source(value))).ToArray(), ValueMember = "Value", DisplayMember = "Text" });
        _mapping.Columns.Add(new DataGridViewComboBoxColumn { Name = "Point", HeaderText = "绑定点位", MinimumWidth = 220, FillWeight = 220,
            DataSource = new[] { new DatabaseUiText.PointItem(Guid.Empty, "不绑定点位") }.Concat(_points).ToArray(), ValueMember = "Id", DisplayMember = "Text" });
        _mapping.Columns.Add(new DataGridViewComboBoxColumn { Name = "FieldType", HeaderText = "转换类型", MinimumWidth = 110, FillWeight = 110,
            DataSource = Enum.GetValues<DatabaseFieldType>().Select(value => new DatabaseUiText.EnumItem<DatabaseFieldType>(value, DatabaseUiText.FieldType(value))).ToArray(), ValueMember = "Value", DisplayMember = "Text" });
        UiTheme.AddColumn(_mapping, "Constant", "手动常量", 150, 150);
        // Combo/check columns also need logical minimum widths registered for DPI scaling.
        if (_mapping is DpiDataGridView dpiGrid)
            foreach (DataGridViewColumn column in _mapping.Columns) dpiGrid.RegisterColumn(column, column.MinimumWidth);
        _mapping.Columns["Include"].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _mapping.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _mapping.CellPainting += PaintConstantInput;
        _mapping.EditingControlShowing += (_, e) =>
        {
            if (e.Control is ComboBox combo)
                combo.DropDownWidth = Math.Min(Screen.FromControl(this).WorkingArea.Width, Math.Max(combo.Width, UiTheme.Scale(this, 360)));
            if (e.Control is TextBox text && _mapping.CurrentCell?.OwningColumn.Name == "Constant")
            {
                text.BorderStyle = BorderStyle.FixedSingle;
                text.BackColor = UiTheme.InputBackground;
                text.ForeColor = UiTheme.Ink;
                text.PlaceholderText = "请输入常量";
            }
        };
        _mapping.CurrentCellDirtyStateChanged += (_, _) => { if (_mapping.IsCurrentCellDirty) _mapping.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _mapping.CellValueChanged += (_, e) =>
        {
            if (_bindingMappings || _updatingMapping || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var row = _mapping.Rows[e.RowIndex];
            if (row.Tag is not DatabaseColumnInfo column || column.IsGenerated) return;
            _updatingMapping = true;
            try
            {
                var source = row.Cells["Source"].Value is DatabaseValueSource selected ? selected : OmittedSource;
                if (_mapping.Columns[e.ColumnIndex].Name == "Include")
                {
                    if (source is not (DatabaseValueSource.Constant or OmittedSource)) _samplingSources[column.Name] = source;
                    row.Cells["Source"].Value = row.Cells["Include"].Value is true
                        ? _samplingSources.GetValueOrDefault(column.Name, DatabaseValueSource.EngineeringValue) : DatabaseValueSource.Constant;
                }
                else if (_mapping.Columns[e.ColumnIndex].Name == "Source")
                {
                    row.Cells["Include"].Value = source is not (DatabaseValueSource.Constant or OmittedSource);
                    if (source is not (DatabaseValueSource.Constant or OmittedSource)) _samplingSources[column.Name] = source;
                }
                else if (_mapping.Columns[e.ColumnIndex].Name == "Constant" && source == OmittedSource)
                    row.Cells["Source"].Value = DatabaseValueSource.Constant;
                UpdateMappingRow(row);
            }
            finally { _updatingMapping = false; }
            if (_mapping.ContainsFocus && _mapping.CurrentRow == row
                && _mapping.Columns[e.ColumnIndex].Name is "Include" or "Source"
                && row.Cells["Source"].Value is DatabaseValueSource.Constant)
                BeginInvoke((Action)(() =>
                {
                    // Finish the checkbox/dropdown notification before moving the editor.
                    // Do not steal focus if the operator has already moved elsewhere.
                    if (IsDisposed || _mapping.IsDisposed || !_mapping.ContainsFocus || row.DataGridView != _mapping
                        || _mapping.CurrentRow != row || _mapping.CurrentCell?.OwningColumn.Name is not ("Include" or "Source")
                        || row.Cells["Source"].Value is not DatabaseValueSource.Constant || row.Cells["Constant"].ReadOnly) return;
                    _mapping.EndEdit(); _mapping.CurrentCell = row.Cells["Constant"]; _mapping.BeginEdit(true);
                }));
        };
    }

    private void PaintConstantInput(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != _mapping.Columns["Constant"].Index
            || _mapping.Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly
            || e.Graphics is not { } graphics || e.CellStyle is not { } style) return;
        e.Paint(e.ClipBounds, e.PaintParts & ~(DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.Focus));
        var inset = UiTheme.Scale(_mapping, 4);
        var frame = Rectangle.Inflate(e.CellBounds, -inset, -inset);
        if (frame.Width > 0 && frame.Height > 0)
        {
            var saved = graphics.Save();
            try
            {
                graphics.SetClip(Rectangle.Intersect(e.ClipBounds, e.CellBounds), System.Drawing.Drawing2D.CombineMode.Intersect);
                using var fill = new SolidBrush(UiTheme.InputBackground);
                var focused = _mapping.ContainsFocus && _mapping.CurrentCell == _mapping.Rows[e.RowIndex].Cells[e.ColumnIndex];
                using var border = new Pen(focused ? UiTheme.Blue : Color.FromArgb(153, 170, 192), Math.Max(1, _mapping.DeviceDpi / 96f));
                graphics.FillRectangle(fill, frame); graphics.DrawRectangle(border, frame);
                var value = e.FormattedValue?.ToString() ?? "";
                var textBounds = Rectangle.Inflate(frame, -UiTheme.Scale(_mapping, 6), 0);
                TextRenderer.DrawText(graphics, value.Length == 0 ? "请输入常量" : value, style.Font ?? _mapping.Font,
                    textBounds, value.Length == 0 ? UiTheme.Muted : UiTheme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            finally { graphics.Restore(saved); }
        }
        e.Handled = true;
    }

    private void BindMappingRows(IReadOnlyList<DatabaseColumnMapping> mappings, IReadOnlyList<DatabaseColumnInfo> columns)
    {
        _bindingMappings = true;
        try
        {
            _mapping.Rows.Clear();
            foreach (var column in columns)
            {
                var mapping = mappings.FirstOrDefault(item => string.Equals(item.Column, column.Name, StringComparison.Ordinal));
                var required = !column.IsNullable && !column.HasDefault && !column.IsGenerated;
                var source = column.IsGenerated ? OmittedSource : mapping?.Source ?? (required ? DatabaseValueSource.EngineeringValue : OmittedSource);
                if (source is not (DatabaseValueSource.Constant or OmittedSource)) _samplingSources[column.Name] = source;
                var index = _mapping.Rows.Add(source is not (DatabaseValueSource.Constant or OmittedSource), column.Name,
                    column.DataType + (column.IsGenerated ? " · 自动生成" : required ? " · 必填" : column.HasDefault ? " · 有默认值" : " · 可空"),
                    source, mapping?.TagId ?? Guid.Empty, mapping?.FieldType ?? DatabaseFieldType.Auto, mapping?.ConstantValue ?? "");
                var row = _mapping.Rows[index]; row.Tag = column;
                if (column.IsGenerated) { row.ReadOnly = true; row.DefaultCellStyle.ForeColor = UiTheme.Muted; }
                UpdateMappingRow(row);
            }
        }
        finally { _bindingMappings = false; }
        _mappingEmpty.Visible = _mapping.Rows.Count == 0;
    }

    private void UpdateMappingRow(DataGridViewRow row)
    {
        if (row.Cells["Source"].Value is not DatabaseValueSource source) return;
        var generated = row.Tag is DatabaseColumnInfo { IsGenerated: true };
        var omitted = source == OmittedSource;
        row.Cells["Point"].ReadOnly = generated || omitted || _mode.SelectedItem is DatabaseStorageMode.PointMapping || !DatabaseUiText.NeedsPoint(source);
        row.Cells["Constant"].ReadOnly = generated || row.Cells["Include"].Value is true;
        row.Cells["FieldType"].ReadOnly = generated || omitted;
        row.Cells["Constant"].ToolTipText = row.Cells["Include"].Value is not true
            ? "填写后作为手动常量写入；空的未配置列保持省略。自动类型按读取到的目标字段类型转换。"
            : "取消勾选“使用采样”后可填写常量。";
        row.Cells["Point"].Style.BackColor = row.Cells["Point"].ReadOnly ? UiTheme.Background : Color.White;
        row.Cells["Constant"].Style.BackColor = row.Cells["Constant"].ReadOnly ? UiTheme.Background : Color.White;
        _mapping.InvalidateCell(row.Cells["Constant"]);
    }

    private IReadOnlyList<DatabaseColumnMapping> CaptureMappings(bool validate)
    {
        if (!_mapping.EndEdit()) throw new EditorValidationException("字段映射输入无效，请完成当前单元格编辑。", _mapping);
        var mappings = new List<DatabaseColumnMapping>();
        var pointMapping = _mode.SelectedItem is DatabaseStorageMode.PointMapping;
        foreach (DataGridViewRow row in _mapping.Rows)
        {
            if (row.Tag is not DatabaseColumnInfo column || column.IsGenerated) continue;
            var source = row.Cells["Source"].Value is DatabaseValueSource selected ? selected : DatabaseValueSource.EngineeringValue;
            if (source == OmittedSource) continue;
            if (row.Cells["Include"].Value is not true) source = DatabaseValueSource.Constant;
            var point = row.Cells["Point"].Value is Guid id && id != Guid.Empty ? id : (Guid?)null;
            if (validate && !pointMapping && DatabaseUiText.NeedsPoint(source) && (point is null || !_points.Any(item => item.Id == point)))
                throw new EditorValidationException($"字段“{row.Cells["Column"].Value}”的值来源为{DatabaseUiText.Source(source)}，请绑定有效点位。", _mapping);
            var type = row.Cells["FieldType"].Value is DatabaseFieldType selectedType ? selectedType : DatabaseFieldType.Auto;
            if (validate && type == DatabaseFieldType.Auto && source is DatabaseValueSource.Constant or DatabaseValueSource.Quality)
                type = DatabaseMappingTools.InferFieldType(column.DataType);
            var mapping = new DatabaseColumnMapping { Column = column.Name, Source = source,
                TagId = !pointMapping && DatabaseUiText.NeedsPoint(source) ? point : null,
                FieldType = type, ConstantValue = row.Cells["Constant"].Value?.ToString() ?? "" };
            if (validate && DatabaseMappingTools.ValidateValue(mapping) is { } error)
            {
                _mapping.CurrentCell = row.Cells[source == DatabaseValueSource.Constant ? "Constant" : "Source"];
                throw new EditorValidationException(error, _mapping);
            }
            mappings.Add(mapping);
        }
        return mappings;
    }

    private void LoadColumns(IReadOnlyList<DatabaseColumnInfo> columns)
    {
        if (columns.Count == 0) throw new InvalidDataException("该表不存在、无可读字段或当前账号没有权限。");
        var mappings = CaptureMappings(false); _columns = columns; _metadataKey = CurrentMetadataKey();
        BindMappingRows(mappings, columns);
    }

    private Task ReadColumnsAsync() => InspectAsync(async () =>
    {
        if (_mode.SelectedItem is DatabaseStorageMode.PointRows)
            throw new InvalidOperationException("请先切换为自定义逐点或自定义整行模式。");
        var connection = CurrentConnection(); var table = CurrentTable();
        var columns = await _application.GetDatabaseColumnsAsync(connection, table);
        LoadColumns(columns);
        Tabs.SelectedTab = _mappingTab;
        _status.Text = $"目标：{connection.Name} / {table}；已读取 {columns.Count} 个字段。勾选使用采样，取消勾选填写常量；自增列由数据库生成。";
    });

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
        if (value.Name.Length > 100 || value.Name.Any(char.IsControl))
            throw new EditorValidationException("写入组名称须为 1–100 字符，不能包含控制字符。", _name);
        if (_application.GetConfiguration().DatabaseWriteGroups.Any(group => group.Id != value.Id
            && string.Equals(group.Name.Trim(), value.Name, StringComparison.OrdinalIgnoreCase)))
            throw new EditorValidationException($"写入组名称“{value.Name}”已存在，请在“写入设置”中使用其他名称。字段映射已保留。", _name);
        value.ConnectionId = CurrentConnection().Id; value.Mode = (DatabaseStorageMode)_mode.SelectedItem!;
        var table = CurrentTable(); value.Schema = table.Schema; value.Table = table.Name;
        value.WriteIntervalMs = (int)_interval.Value; value.MaxSampleAgeMs = (int)_maxAge.Value; value.RequireGoodQuality = _goodOnly.Checked;
        value.TagIds = _allPoints.Checked ? [] : _selectedPoints.ToList();
        if (value.Mode != DatabaseStorageMode.ColumnMapping && !_allPoints.Checked && value.TagIds.Count == 0)
            throw new EditorValidationException("请选择至少一个点位，或勾选“全部启用点位”。", _allPoints);
        if (value.Mode == DatabaseStorageMode.PointRows)
        {
            value.Columns = [];
        }
        else
        {
            if (_metadataKey != CurrentMetadataKey()) throw new EditorValidationException("保存自定义字段映射前，请读取当前目标表的字段并核对必填列。", _mappingLoadColumns);
            value.Columns = CaptureMappings(true).ToList();
            if (value.Mode == DatabaseStorageMode.ColumnMapping) value.TagIds = [];
            if (value.Columns.Count == 0) throw new EditorValidationException("请至少选择一个要写入的字段。", _mapping);
            var missing = _columns.Where(column => !column.IsNullable && !column.HasDefault && !column.IsGenerated
                && !value.Columns.Any(mapping => string.Equals(mapping.Column, column.Name, StringComparison.Ordinal))).Select(column => column.Name).ToArray();
            if (missing.Length > 0) throw new EditorValidationException("以下必填列尚未映射：" + string.Join("、", missing), _mapping);
            if (!value.Columns.Any(mapping => DatabaseUiText.NeedsPoint(mapping.Source)))
                throw new EditorValidationException("自定义映射至少需要一个采样来源字段；只有新采样到达才会触发写入。", _mapping);
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
