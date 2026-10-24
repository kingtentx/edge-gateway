using EdgeGateway.Model;
using System.Diagnostics;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private static string RecordingSmokeJson<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, GatewayJson.Options);
    private static void AssertRecordingSmokeUnchanged<T>(string before, T value, string context)
    { if (before != RecordingSmokeJson(value)) throw new InvalidOperationException(context + ": editor mutated the supplied configuration."); }

    private static DatabaseConnectionConfiguration RecordingConnectionFixture() => new()
    {
        Name = "采集数据库验证", Enabled = false, Provider = ExternalDatabaseProvider.PostgreSql,
        Host = "localhost", Port = 5432, Database = "edge_gateway", Username = "edge_app",
        PasswordEnvironmentVariable = "EDGE_DATABASE_PASSWORD", UseTls = true,
        QueueCapacityRows = 2000, QueueCapacityMb = 32, MaxRetryAttempts = 3
    };

    private static DatabaseWriteGroupConfiguration RecordingMappingFixture(Guid connectionId, Guid tagId) => new()
    {
        Name = "工艺字段验证", Enabled = false, ConnectionId = connectionId, Mode = DatabaseStorageMode.ColumnMapping,
        Schema = "public", Table = "process_values",
        Columns = [
            new() { Column = "event_id", Source = DatabaseValueSource.EventId, FieldType = DatabaseFieldType.Text },
            new() { Column = "recorded_at", Source = DatabaseValueSource.RecordTime, FieldType = DatabaseFieldType.Timestamp },
            new() { Column = "temperature", Source = DatabaseValueSource.EngineeringValue, TagId = tagId, FieldType = DatabaseFieldType.Double },
            new() { Column = "note", Source = DatabaseValueSource.Constant, FieldType = DatabaseFieldType.Text, ConstantValue = "生产一线" }
        ]
    };

    // Explicit metadata fixtures exercise the editor only. Provider schema discovery,
    // DDL and writes require the separate external-database integration suite.
    private static IReadOnlyList<DatabaseColumnInfo> RecordingColumnFixtures() => [
        new("id", "bigint", false, IsGenerated: true), new("event_id", "varchar(64)", false),
        new("recorded_at", "timestamp", false), new("temperature", "double", false), new("note", "varchar(200)", true)
    ];

    private async Task SmokeDataRecordingAsync()
    {
        if (!_smokeTest || _application.IsRunning || _dirty || _loggingDirty)
            throw new InvalidOperationException("Data recording smoke requires an isolated, stopped application without drafts.");
        var original = Clone(_configuration); var originalPage = _activePage; var originalSelection = SelectedDevice()?.Id;
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Data recording smoke: " + message); }
        try
        {
            var device = new DeviceConfiguration
            {
                Name = "记录策略验证设备", DriverId = "simulator", PollIntervalMs = 100,
                Tags = [new() { Name = "温度", Address = "constant:25", DataType = TagDataType.Float64, Unit = "℃" },
                        new() { Name = "计数", Address = "ramp", DataType = TagDataType.Float64 }]
            };
            var fixture = Clone(original); fixture.AutoStart = false; fixture.Mqtt = new MqttConfiguration { Enabled = false };
            fixture.Devices = [device]; fixture.Channels = []; fixture.Databases = []; fixture.DatabaseWriteGroups = [];
            fixture.Logging = new GatewayLoggingConfiguration();
            await CommitConfigurationAsync(fixture, device.Id, replaceCloudDraft: true);
            _executedHistoryQuery = null; _executedHistoryGroupId = null; _queriedHistory = []; BindHistoryGroups();
            ShowPage("history");
            Check(!_historyQuery.Enabled && _historySummary.Text.Contains("尚未配置"), "no-database history must show an empty state.");
            Check(_configuration.Logging.LogAcquisitionSamples, "new configuration did not default to acquisition logs.");

            var connection = RecordingConnectionFixture(); var originalConnection = RecordingSmokeJson(connection);
            using (var editor = new DatabaseConnectionDialog(connection, _application))
            {
                editor.SetApplyHandler(async () => CompleteConfigurationCommit(await _application.ApplyDatabaseSettingsAsync([editor.Value], [])), false, _ => { });
                editor.Show(this);
                try
                {
                    VerifyEditorLayout(editor, "database connection");
                    foreach (var input in editor.SmokeInputs) BringIntoViewAndAssert(input, "database connection " + input.Name);
                    Check(editor.SmokeInputs.Count() == 16, "connection dialog is missing queue or retry settings.");
                    var password = (TextBox)editor.SmokeInputs.Single(control => control.Name == "DatabasePasswordEnvironment");
                    password.Text = "password;not-an-environment-name";
                    var rejected = false;
                    try { editor.CommitForSmoke(); } catch (EditorValidationException) { rejected = true; }
                    Check(rejected, "password input accepted a literal/connection-string fragment.");
                    password.Text = connection.PasswordEnvironmentVariable;
                    var providerInput = (ComboBox)editor.SmokeInputs.Single(control => control.Name == "DatabaseProvider");
                    foreach (var provider in Enum.GetValues<ExternalDatabaseProvider>())
                    {
                        providerInput.SelectedItem = provider;
                        var value = editor.CommitForSmoke();
                        Check(value.Provider == provider && value.Port == (provider switch
                            { ExternalDatabaseProvider.MySql => 3306, ExternalDatabaseProvider.PostgreSql => 5432, _ => 1521 }),
                            "provider selection did not round-trip its value and default port.");
                    }
                    providerInput.SelectedItem = connection.Provider;
                    var before = DeviceImportSmokeJson(_configuration);
                    using (RejectConfigurationWritesForSmoke())
                    {
                        rejected = false;
                        try { await editor.ApplyAndAcceptForSmokeAsync(); }
                        catch (Exception exception) when (IsConfigurationWriteFailureForSmoke(exception)) { rejected = true; }
                        Check(rejected && editor.Visible && password.Text == connection.PasswordEnvironmentVariable
                            && DeviceImportSmokeJson(_configuration) == before, "failed connection save lost draft inputs or changed applied configuration.");
                        await AssertSavedConfigurationForSmokeAsync("rejected database connection save");
                    }
                    BringIntoViewAndAssert(password, "retained database password variable");
                    SaveEditorScreenshot(editor, "recording", "connection");
                    await editor.ApplyAndAcceptForSmokeAsync();
                    Check(!_application.IsRunning && _configuration.Databases.Single().Id == connection.Id, "database save started collection or lost connection identity.");
                }
                finally { editor.Close(); }
            }
            AssertRecordingSmokeUnchanged(originalConnection, connection, "database connection draft isolation");

            var group = new DatabaseWriteGroupConfiguration { Name = "逐点采样验证", ConnectionId = connection.Id, Schema = "public", TagIds = [device.Tags[0].Id] };
            var originalGroup = RecordingSmokeJson(group);
            using (var editor = new DatabaseWriteGroupDialog(group, [connection], [device], _application))
            {
                editor.SetApplyHandler(async () => CompleteConfigurationCommit(await _application.ApplyDatabaseSettingsAsync([connection], [editor.Value])), false, _ => { });
                editor.Show(this);
                try
                {
                    VerifyEditorLayout(editor, "standard point rows");
                    VerifyDatabasePointSelectionForSmoke(editor, "standard point rows");
                    Check(editor.CommitForSmoke().TagIds.SequenceEqual(group.TagIds), "standard group did not preserve explicit point selection.");
                    var allPoints = Descendants(editor).OfType<CheckBox>().Single(control => control.Name == "DatabaseAllEnabledPoints");
                    allPoints.Checked = true;
                    Check(editor.CommitForSmoke().TagIds.Count == 0, "all-enabled mode should persist an empty point filter.");
                    allPoints.Checked = false;
                    Check(editor.CommitForSmoke().TagIds.SequenceEqual(group.TagIds), "all-enabled toggle lost the explicit point draft.");
                    SaveEditorScreenshot(editor, "recording", "point-rows");
                    await editor.ApplyAndAcceptForSmokeAsync();
                }
                finally { editor.Close(); }
            }
            AssertRecordingSmokeUnchanged(originalGroup, group, "database point selection draft isolation");
            var deleteWarning = DatabasePointReferenceWarning([device.Tags[0]]);
            Check(deleteWarning is not null && deleteWarning.Contains(group.Name) && deleteWarning.Contains(device.Tags[0].Name)
                && deleteWarning.Contains("采集写入组"), "mapped-point deletion did not identify the write group and the corrective action.");

            var mapping = RecordingMappingFixture(connection.Id, device.Tags[0].Id); var originalMapping = RecordingSmokeJson(mapping);
            using (var editor = new DatabaseWriteGroupDialog(mapping, [connection], [device], _application))
            {
                editor.Show(this);
                try
                {
                    var rejected = false;
                    try { editor.CommitForSmoke(); } catch (EditorValidationException) { rejected = true; }
                    Check(rejected, "custom mapping saved before reading target metadata.");
                    editor.LoadColumnsForSmoke(RecordingColumnFixtures());
                    VerifyEditorLayout(editor, "custom column mapping");
                    VerifyDatabaseMappingForSmoke(editor, "custom column mapping");
                    VerifyDatabaseOptionBindingsForSmoke(editor, device.Tags[0].Id);
                    var temperature = editor.SmokeMapping.Rows.Cast<DataGridViewRow>().Single(row => (string)row.Cells["Column"].Value == "temperature");
                    temperature.Cells["Include"].Value = false; rejected = false;
                    try { editor.CommitForSmoke(); } catch (EditorValidationException) { rejected = true; }
                    Check(rejected, "custom mapping accepted an omitted non-null, non-default field.");
                    temperature.Cells["Include"].Value = true; temperature.Cells["Point"].Value = Guid.Empty; rejected = false;
                    try { editor.CommitForSmoke(); } catch (EditorValidationException) { rejected = true; }
                    Check(rejected, "custom mapping accepted an unbound point source.");
                    temperature.Cells["Point"].Value = device.Tags[0].Id;
                    var value = editor.CommitForSmoke();
                    Check(value.Columns.Count == 4 && value.Columns.All(column => column.Column != "id")
                        && value.Columns.Single(column => column.Column == "note").ConstantValue == "生产一线",
                        "generated columns or typed constants were captured incorrectly.");
                    editor.SmokeMapping.CurrentCell = editor.SmokeMapping.Rows[1].Cells["Column"];
                    SaveEditorScreenshot(editor, "recording", "column-mapping");
                }
                finally { editor.Close(); }
            }
            AssertRecordingSmokeUnchanged(originalMapping, mapping, "custom mapping draft isolation");

            // Availability can be checked with acquisition stopped and without a server.
            var enabledConnection = GatewayJson.Clone(connection); enabledConnection.Enabled = true;
            var enabledGroup = GatewayJson.Clone(group); enabledGroup.Enabled = true;
            CompleteConfigurationCommit(await _application.ApplyDatabaseSettingsAsync([enabledConnection], [enabledGroup]));
            Check(_historyGroup.Items.Count == 1 && _historyQuery.Enabled, "enabled standard table was not offered as a history source.");
            CompleteConfigurationCommit(await _application.ApplyDatabaseSettingsAsync([connection], [group]));

            await _application.StartDeviceAsync(device.Id); UpdateCommands();
            var timer = Stopwatch.StartNew();
            while (!_application.GetLatest().Any(sample => sample.DeviceId == device.Id && sample.Quality == SampleQuality.Good)
                   && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Check(_application.IsDeviceRunning(device.Id) && _application.GetLatest().Any(sample => sample.DeviceId == device.Id && sample.Quality == SampleQuality.Good), "simulator did not begin acquisition.");
            var rejectedWhileRunning = false;
            try { await _application.ApplyDatabaseSettingsAsync([connection], [group]); } catch (InvalidOperationException) { rejectedWhileRunning = true; }
            Check(rejectedWhileRunning, "database configuration changed while acquisition was active.");
            ShowPage("logs"); _logTabs.SelectedIndex = 1;
            ((NumericUpDown)_loggingFields["RetainedDays"]).Value = 17;
            ((NumericUpDown)_loggingFields["MaxFileSizeMb"]).Value = 12;
            Check(_loggingDirty && _loggingApply.Enabled, "logging controls did not support a live draft.");
            using (RejectConfigurationWritesForSmoke())
            {
                var rejected = false;
                try { await ApplyLoggingSettingsAsync(); } catch (Exception exception) when (IsConfigurationWriteFailureForSmoke(exception)) { rejected = true; }
                Check(rejected && _loggingDirty && ((NumericUpDown)_loggingFields["RetainedDays"]).Value == 17
                    && _configuration.Logging.RetainedDays == 30 && _application.IsDeviceRunning(device.Id), "failed logging save lost the draft or stopped acquisition.");
                await AssertSavedConfigurationForSmokeAsync("rejected logging save");
            }
            var beforeSample = _application.GetLatest().Max(sample => sample.Timestamp);
            await ApplyLoggingSettingsAsync();
            Check(!_loggingDirty && _configuration.Logging.RetainedDays == 17 && _configuration.Logging.MaxFileSizeMb == 12
                && _application.IsDeviceRunning(device.Id), "logging hot apply did not persist or interrupted the device.");
            timer.Restart();
            while (!_application.GetLatest().Any(sample => sample.Timestamp > beforeSample) && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Check(_application.GetLatest().Any(sample => sample.Timestamp > beforeSample), "device did not continue producing samples after logging apply.");
            foreach (var input in _loggingFields.Values) BringIntoViewAndAssert(input, "logging settings " + input.Name);
            ResetScrollPositions(_pages["logs"], _navigation["overview"]); _logTabs.SelectedIndex = 1;
            SaveEditorScreenshot(this, "recording", "log-settings");
            await AssertSavedConfigurationForSmokeAsync("data recording configuration");
            Console.WriteLine($"Data recording configuration smoke passed: connection save failure/retry, 16 connection inputs, standard point selection, custom required/generated columns and point/type/constant bindings, history empty/source states, stopped-only database apply, and logging hot apply with retained failure drafts at actual {DeviceDpi} DPI; metadata fixtures tested UI only, no external database was contacted.");
        }
        finally
        {
            await _application.StopAsync(); _loggingDirty = false;
            await RestoreConfigurationForSmokeAsync(original);
            _executedHistoryQuery = null; _executedHistoryGroupId = null; _queriedHistory = []; _historyGrid.DataSource = null;
            _trend.Visible = false; if (_trend.Parent is TableLayoutPanel historyLayout) historyLayout.RowStyles[0].Height = 0;
            _logTabs.SelectedIndex = 0; RebindConfigurationViews(originalSelection); ShowPage(originalPage); UpdateCommands();
        }
    }

    private static void VerifyDatabaseOptionBindingsForSmoke(DatabaseWriteGroupDialog editor, Guid pointId)
    {
        editor.ShowMappingForSmoke(); var grid = editor.SmokeMapping;
        var temperature = grid.Rows.Cast<DataGridViewRow>().Single(row => (string)row.Cells["Column"].Value == "temperature");
        var note = grid.Rows.Cast<DataGridViewRow>().Single(row => (string)row.Cells["Column"].Value == "note");
        // Keep one genuine point binding while checking non-point value sources.
        note.Cells["Source"].Value = DatabaseValueSource.EngineeringValue; note.Cells["Point"].Value = pointId;
        void Select(DataGridViewRow row, string column, object value)
        {
            var cell = row.Cells[column]; grid.CurrentCell = cell;
            FocusForSmoke(grid, grid, editor, "database option " + column);
            if (!grid.BeginEdit(true) || grid.EditingControl is not ComboBox choice)
                throw new InvalidOperationException("Database mapping combo cannot enter edit mode: " + column);
            choice.SelectedValue = value;
            if (choice.SelectedIndex < 0 || !Equals(choice.SelectedValue, value))
                throw new InvalidOperationException("Database mapping option is absent from its actual dropdown: " + value);
            grid.NotifyCurrentCellDirty(true); grid.CommitEdit(DataGridViewDataErrorContexts.Commit); grid.EndEdit();
        }
        foreach (var source in Enum.GetValues<DatabaseValueSource>())
        {
            Select(temperature, "Source", source);
            if (DatabaseUiText.NeedsPoint(source)) Select(temperature, "Point", pointId);
            var captured = editor.CommitForSmoke().Columns.Single(column => column.Column == "temperature");
            if (captured.Source != source || (DatabaseUiText.NeedsPoint(source) ? captured.TagId != pointId : captured.TagId is not null))
                throw new InvalidOperationException("Database value-source binding did not round-trip: " + source);
        }
        Select(temperature, "Source", DatabaseValueSource.Constant);
        foreach (var type in Enum.GetValues<DatabaseFieldType>())
        {
            Select(temperature, "FieldType", type);
            temperature.Cells["Constant"].Value = type switch
            { DatabaseFieldType.Boolean => "true", DatabaseFieldType.Timestamp => "2026-10-24T12:00:00Z", DatabaseFieldType.Decimal or DatabaseFieldType.Double => "12.5", _ => "12" };
            var captured = editor.CommitForSmoke().Columns.Single(column => column.Column == "temperature");
            if (captured.FieldType != type || captured.ConstantValue != (string)temperature.Cells["Constant"].Value)
                throw new InvalidOperationException("Database conversion/constant binding did not round-trip: " + type);
        }
        Select(temperature, "Source", DatabaseValueSource.EngineeringValue); Select(temperature, "Point", pointId);
        Select(temperature, "FieldType", DatabaseFieldType.Double); temperature.Cells["Constant"].Value = "";
        Select(note, "Source", DatabaseValueSource.Constant); Select(note, "FieldType", DatabaseFieldType.Text);
        note.Cells["Constant"].Value = "生产一线";
    }

    private static void VerifyDatabasePointSelectionForSmoke(DatabaseWriteGroupDialog editor, string context)
    {
        editor.SelectModeForSmoke(DatabaseStorageMode.PointRows);
        BringIntoViewAndAssert(editor.SmokePointList, context + " point selector");
        var list = editor.SmokePointList;
        if (!list.Enabled || list.Items.Count == 0) throw new InvalidOperationException(context + ": explicit point selection is not available.");
        foreach (var index in new[] { 0, list.Items.Count - 1 }.Distinct())
        {
            list.SelectedIndex = index; list.TopIndex = index;
            FocusForSmoke(list, list, editor, context + " point item");
            var bounds = list.GetItemRectangle(index);
            if (!list.ContainsFocus || !list.ClientRectangle.Contains(bounds))
                throw new InvalidOperationException(context + ": point item cannot be focused and fully displayed.");
        }
    }

    private static int VerifyDatabaseMappingForSmoke(DatabaseWriteGroupDialog editor, string context)
    {
        editor.SelectModeForSmoke(DatabaseStorageMode.ColumnMapping); editor.ShowMappingForSmoke();
        var grid = editor.SmokeMapping; BringIntoViewAndAssert(grid, context + " mapping grid");
        var row = grid.Rows.Cast<DataGridViewRow>().Single(item => (string)item.Cells["Column"].Value == "temperature");
        foreach (DataGridViewCell cell in row.Cells)
        {
            FocusForSmoke(grid, grid, editor, context + " mapping " + cell.OwningColumn.Name);
            PerformLayoutRecursively(editor); grid.CurrentCell = null; grid.CurrentCell = cell;
            grid.FirstDisplayedScrollingRowIndex = row.Index; grid.FirstDisplayedScrollingColumnIndex = cell.ColumnIndex;
            var bounds = grid.GetCellDisplayRectangle(cell.ColumnIndex, row.Index, false);
            var clipped = grid.GetCellDisplayRectangle(cell.ColumnIndex, row.Index, true);
            if (!grid.ContainsFocus || grid.CurrentCell != cell || bounds.Width <= 0 || bounds.Height <= 0 || bounds != clipped)
                throw new InvalidOperationException($"{context}: mapping {cell.OwningColumn.Name} cannot be focused and fully displayed: {bounds}; clipped {clipped}.");
            var screenBounds = grid.RectangleToScreen(bounds);
            for (Control? parent = grid; parent is not null; parent = parent.Parent)
                if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(screenBounds))
                    throw new InvalidOperationException(context + ": mapping cell clipped by " + parent.Name);
        }
        grid.EndEdit(); return row.Cells.Count;
    }
}
