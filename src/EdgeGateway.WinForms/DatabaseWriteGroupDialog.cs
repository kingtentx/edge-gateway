using EdgeGateway.Application;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal sealed partial class DatabaseWriteGroupDialog : EditorDialog
{
    private readonly DatabaseWriteGroupConfiguration _source;
    private readonly IGatewayApplication _application;
    private readonly IReadOnlyList<DatabaseConnectionConfiguration> _connections;
    private readonly IReadOnlyList<DatabaseUiText.PointItem> _points;
    private readonly HashSet<Guid> _selectedPoints;
    private IReadOnlyList<DatabaseColumnInfo> _columns = [];
    private string? _metadataKey;
    private bool _loading, _bindingPoints, _bindingTable;
    private bool _bindingMappings, _updatingMapping;
    // UI-only option; omitted columns are never persisted as an invalid source enum.
    private const DatabaseValueSource OmittedSource = (DatabaseValueSource)(-1);
    private readonly Dictionary<string, DatabaseValueSource> _samplingSources = new(StringComparer.Ordinal);
    internal DatabaseWriteGroupConfiguration Value { get; private set; } = null!;
    internal DataGridView SmokeMapping => _mapping;
    internal CheckedListBox SmokePointList => _pointList;

    public DatabaseWriteGroupDialog() : base("数据库写入组", "配置写入目标、点位选择与字段映射。")
    { _source = new(); _connections = []; _points = []; _selectedPoints = []; _application = null!; InitializeComponent(); }

    internal DatabaseWriteGroupDialog(DatabaseWriteGroupConfiguration configuration,
        IReadOnlyList<DatabaseConnectionConfiguration> connections, IReadOnlyList<DeviceConfiguration> devices, IGatewayApplication application)
        : base("数据库写入组", "标准点行保存每个采样结果；自定义字段映射使用已有业务表。保存前需停止全部设备。")
    {
        ConfigureWindow(new Size(1024, 680), new Size(604, 481));
        _source = GatewayJson.Clone(configuration); _connections = GatewayJson.Clone(connections); _application = application;
        _selectedPoints = _source.TagIds.ToHashSet();
        _points = devices.SelectMany(device => device.Tags.Select(tag => new DatabaseUiText.PointItem(tag.Id,
            device.Name + " / " + tag.Name + (!device.Enabled || !tag.Enabled ? "（未启用）" : "")))).ToArray();
        InitializeComponent();
        _name.Text = _source.Name; _enabled.Checked = _source.Enabled;
        BindChoice(_connection, _connections.Select(c => new ConnectionChoice(c.Id, c.Name + " · " + DatabaseUiText.Provider(c.Provider))).Cast<object>().ToArray(), null);
        for (var i = 0; i < _connection.Items.Count; i++) if (_connection.Items[i] is ConnectionChoice choice && choice.Id == _source.ConnectionId) _connection.SelectedIndex = i;
        BindChoice(_mode, Enum.GetValues<DatabaseStorageMode>().Cast<object>().ToArray(), _source.Mode);
        _mode.Format += (_, e) => { if (e.ListItem is DatabaseStorageMode value) e.Value = DatabaseUiText.Mode(value); };
        _schema.Text = _source.Schema; _table.Text = _source.Table;
        _interval.Value = Math.Clamp(_source.WriteIntervalMs, 100, 3600000);
        _maxAge.Value = Math.Clamp(_source.MaxSampleAgeMs, 100, 86400000);
        _goodOnly.Checked = _source.RequireGoodQuality; _allPoints.Checked = _source.TagIds.Count == 0;
        listTablesButton.Click += async (_, _) => await InspectAsync(async () =>
        {
            var tables = await _application.GetDatabaseTablesAsync(CurrentConnection());
            _bindingTable = true;
            try { _tables.Items.Clear(); _tables.Items.AddRange(tables.Cast<object>().ToArray()); }
            finally { _bindingTable = false; }
            _status.Text = $"读取到 {tables.Count} 张表。可从列表选择目标表，或直接填写表名。";
        });
        previewButton.Click += async (_, _) =>
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
        };
        _loadColumns.Click += async (_, _) => await ReadColumnsAsync();
        _createTable.Click += async (_, _) => await CreateTableAsync();
        _mappingLoadColumns.Click += async (_, _) => await ReadColumnsAsync();
        _switchPointMapping.Click += (_, _) => _mode.SelectedItem = DatabaseStorageMode.PointMapping;
        ConfigureMappingGrid();
        _pointList.ItemCheck += (_, e) => { if (_bindingPoints) return; var id = ((DatabaseUiText.PointItem)_pointList.Items[e.Index]).Id; if (e.NewValue == CheckState.Checked) _selectedPoints.Add(id); else _selectedPoints.Remove(id); };
        _pointSearch.TextChanged += (_, _) => BindPoints(); _allPoints.CheckedChanged += (_, _) => _pointList.Enabled = !_allPoints.Checked;
        BindPoints(); _pointList.Enabled = !_allPoints.Checked;

        mappingLayout.SizeChanged += (_, _) =>
        {
            foreach (var label in new[] { _mappingHint, _mappingStatus })
                label.MaximumSize = new Size(Math.Max(1, mappingLayout.ClientSize.Width - label.Margin.Horizontal), 0);
        };
        _status.TextChanged += (_, _) => _mappingStatus.Text = _status.Text;
        _status.ForeColorChanged += (_, _) => _mappingStatus.ForeColor = _status.ForeColor;
        _mappingStatus.Text = _status.Text;
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
        ((DataGridViewComboBoxColumn)_mapping.Columns["Source"]).DataSource = Enumerable.Append(Enum.GetValues<DatabaseValueSource>(), OmittedSource).Select(value => new DatabaseUiText.EnumItem<DatabaseValueSource>(value, DatabaseUiText.Source(value))).ToArray();
        ((DataGridViewComboBoxColumn)_mapping.Columns["Point"]).DataSource = new[] { new DatabaseUiText.PointItem(Guid.Empty, "不绑定点位") }.Concat(_points).ToArray();
        ((DataGridViewComboBoxColumn)_mapping.Columns["FieldType"]).DataSource = Enum.GetValues<DatabaseFieldType>().Select(value => new DatabaseUiText.EnumItem<DatabaseFieldType>(value, DatabaseUiText.FieldType(value))).ToArray();
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
            if (e.Control is GatewayInput text && _mapping.CurrentCell?.OwningColumn.Name == "Constant")
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
            if (MessageBox.Show(this, $"在数据库“{connection.Name}”中创建数据表 {table}？\n将使用标准点行结构。已存在的表不会自动替换或修改；请核对目标与账号权限。", "确认创建数据表", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
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
