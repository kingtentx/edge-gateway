using EdgeGateway.Model;
using System.Diagnostics;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeCustomPointMappingAsync()
    {
        if (!_smokeTest) throw new InvalidOperationException("Database mapping smoke requires an isolated smoke host.");
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Custom point mapping: " + message); }
        var connection = RecordingConnectionFixture();
        var device = new DeviceConfiguration { Name = "映射验证设备", DriverId = "simulator",
            Tags = [new() { Name = "温度" }, new() { Name = "计数" }] };
        var original = new DatabaseWriteGroupConfiguration { ConnectionId = connection.Id };
        var other = new DatabaseWriteGroupConfiguration { ConnectionId = connection.Id, Name = "采集数据（2）" };
        await _application.ApplyDatabaseSettingsAsync([connection], [original, other]);
        var group = DatabaseUiText.NewWriteGroup(connection.Id, _application.GetConfiguration().DatabaseWriteGroups);
        Check(group.Name == "采集数据（3）" && group.Id != original.Id && group.Id != other.Id, "new group name or stable identity collides.");
        group.Table = "custom_samples"; group.TagIds = device.Tags.Select(tag => tag.Id).ToList();
        using (var existing = new DatabaseWriteGroupDialog(original, [connection], [device], _application))
            Check(existing.CommitForSmoke().Id == original.Id, "editing an existing group falsely rejects its own name or changes its identity.");
        var before = RecordingSmokeJson(group);
        using var editor = new DatabaseWriteGroupDialog(group, [connection], [device], _application);
        editor.Show(this);
        try
        {
            editor.ShowMappingForSmoke();
            var switchMode = Descendants(editor).OfType<Button>().Single(control => control.Name == "UsePointMapping");
            BringIntoViewAndAssert(switchMode, "switch to custom point mapping");
            Check(switchMode.Enabled && switchMode.Visible, "standard mode has no reachable mapping action.");
            switchMode.PerformClick();
            var readColumns = Descendants(editor).OfType<Button>().Single(control => control.Name == "RefreshMappingColumns");
            BringIntoViewAndAssert(readColumns, "read mapping columns");
            Check(readColumns.Enabled, "field-read action remains disabled after switching modes.");
            var rejected = false;
            try { editor.CommitForSmoke(); } catch (EditorValidationException) { rejected = true; }
            Check(rejected, "unread metadata was accepted.");
            DatabaseColumnInfo[] columns = [new("id", "bigint", false, IsGenerated: true),
                new("_NAME", "varchar", true), new("_NUMERICID", "varchar", true), new("_VALUE", "varchar", true),
                new("_TIMESTAMP", "timestamp", true), new("_QUALITY", "int", true), new("optional_note", "varchar", true, HasDefault: true)];
            editor.LoadColumnsForSmoke(columns);
            var grid = editor.SmokeMapping;
            Check(!grid.Columns["Point"].Visible, "point mode still asks each field for a fixed point binding.");
            var generated = grid.Rows.Cast<DataGridViewRow>().Single(row => Equals(row.Cells["Column"].Value, "id"));
            Check(generated.ReadOnly && generated.Cells["Include"].Value is false, "auto-increment id is writable.");
            var optional = grid.Rows.Cast<DataGridViewRow>().Single(row => Equals(row.Cells["Column"].Value, "optional_note"));
            Check(optional.Cells["Include"].Value is false && !optional.Cells["Constant"].ReadOnly,
                "an initially unchecked optional field cannot accept a manual constant.");
            optional.Cells["Constant"].Value = "line-A";
            Check(Equals(optional.Cells["Source"].Value, DatabaseValueSource.Constant), "typing an unchecked constant did not include the column.");
            optional.Cells["Source"].Value = (DatabaseValueSource)(-1);
            var choices = new[]
            {
                ("_NAME", DatabaseValueSource.TagName, DatabaseFieldType.Text),
                ("_NUMERICID", DatabaseValueSource.DeviceName, DatabaseFieldType.Text),
                ("_VALUE", DatabaseValueSource.EngineeringValue, DatabaseFieldType.Text),
                ("_TIMESTAMP", DatabaseValueSource.SampleTime, DatabaseFieldType.Timestamp),
                ("_QUALITY", DatabaseValueSource.Quality, DatabaseFieldType.Auto)
            };
            foreach (var (column, source, type) in choices)
            {
                var row = grid.Rows.Cast<DataGridViewRow>().Single(item => Equals(item.Cells["Column"].Value, column));
                row.Cells["Include"].Value = true;
                foreach (var (name, value) in new[] { ("Source", (object)source), ("FieldType", (object)type) })
                {
                    grid.CurrentCell = row.Cells[name];
                    FocusForSmoke(grid, grid, editor, "custom point " + name);
                    Check(grid.BeginEdit(true) && grid.EditingControl is ComboBox, "mapping dropdown cannot enter edit mode.");
                    ((ComboBox)grid.EditingControl!).SelectedValue = value;
                    grid.NotifyCurrentCellDirty(true); grid.CommitEdit(DataGridViewDataErrorContexts.Commit); grid.EndEdit();
                    Check(Equals(row.Cells[name].Value, value), "mapping dropdown value was not retained.");
                }
            }
            var quality = grid.Rows.Cast<DataGridViewRow>().Single(row => Equals(row.Cells["Column"].Value, "_QUALITY"));
            rejected = false;
            try { editor.CommitForSmoke(); } catch (EditorValidationException exception) { rejected = exception.Message.Contains("Good / Bad / Uncertain"); }
            Check(rejected, "quality text was allowed into an integer database column.");
            grid.EndEdit(); quality.Cells["Include"].Value = false;
            Check(Equals(quality.Cells["Source"].Value, DatabaseValueSource.Constant) && !quality.Cells["Constant"].ReadOnly,
                "unchecking sampling did not enable the constant source/editor.");
            grid.CurrentCell = quality.Cells["Constant"];
            FocusForSmoke(grid, grid, editor, "manual quality constant");
            Check(grid.BeginEdit(true) && grid.EditingControl is TextBox, "manual constant cannot enter edit mode.");
            Check(((TextBox)grid.EditingControl!).BorderStyle == BorderStyle.FixedSingle
                && ((TextBox)grid.EditingControl!).PlaceholderText == "请输入常量", "manual constant editor has no visible input border or placeholder.");
            ((TextBox)grid.EditingControl!).Text = "192"; grid.EndEdit();
            quality.Cells["Include"].Value = true;
            Check(Equals(quality.Cells["Source"].Value, DatabaseValueSource.Quality) && Equals(quality.Cells["Constant"].Value, "192"),
                "switching back to sampling lost the original source or constant draft.");
            quality.Cells["Include"].Value = false;
            quality.Cells["Constant"].Value = "Good"; rejected = false;
            try { editor.CommitForSmoke(); } catch (EditorValidationException exception) { rejected = exception.Message.Contains("常量"); }
            Check(rejected, "invalid integer constant was not rejected before save.");
            grid.EndEdit(); quality.Cells["Constant"].Value = "192";
            Check(DatabaseUiText.Source(DatabaseValueSource.RawValue) == "raw_value [原始值]"
                && DatabaseUiText.Source(DatabaseValueSource.SampleTime) == "timestamp [采样时间]", "source labels do not show real standard column names.");
            var valueBeforeRefresh = editor.CommitForSmoke();
            Check(valueBeforeRefresh.Mode == DatabaseStorageMode.PointMapping && valueBeforeRefresh.TagIds.SequenceEqual(group.TagIds)
                && valueBeforeRefresh.Columns.Count == 5 && valueBeforeRefresh.Columns.All(mapping => mapping.TagId is null)
                && valueBeforeRefresh.Columns.Single(mapping => mapping.Column == "_QUALITY") is { Source: DatabaseValueSource.Constant, FieldType: DatabaseFieldType.Int64, ConstantValue: "192" },
                "point selection, field sources, or typed constant did not round-trip.");
            editor.LoadColumnsForSmoke(columns);
            Check(RecordingSmokeJson(valueBeforeRefresh) == RecordingSmokeJson(editor.CommitForSmoke()), "refresh lost mapping drafts.");
            quality = grid.Rows.Cast<DataGridViewRow>().Single(row => Equals(row.Cells["Column"].Value, "_QUALITY"));
            Check(quality.Cells["Include"].Value is false && !quality.Cells["Constant"].ReadOnly,
                "refresh re-enabled sampling or disabled an unchecked constant.");
            using (var reopened = new DatabaseWriteGroupDialog(valueBeforeRefresh, [connection], [device], _application))
            {
                reopened.LoadColumnsForSmoke(columns);
                Check(RecordingSmokeJson(valueBeforeRefresh) == RecordingSmokeJson(reopened.CommitForSmoke()), "reopening lost the manual constant or omitted columns.");
            }
            var nameInput = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "DatabaseGroupName");
            nameInput.Text = "  " + original.Name + "  ";
            editor.ShowMappingForSmoke();
            EditorValidationException? nameError = null;
            try { await editor.ApplyAndAcceptForSmokeAsync(); } catch (EditorValidationException exception) { nameError = exception; }
            Check(nameError?.Field == nameInput && nameError.Message.Contains("已存在") && nameInput.Focused,
                "duplicate group name did not reveal and focus the name input from the mapping tab.");
            Check(editor.Visible && editor.DialogResult != DialogResult.OK && _application.GetConfiguration().DatabaseWriteGroups.Count == 2,
                "rejected save closed the dialog or changed the saved groups.");
            nameInput.Text = group.Name;
            Check(RecordingSmokeJson(valueBeforeRefresh) == RecordingSmokeJson(editor.CommitForSmoke()), "name validation lost mapping drafts.");
            editor.ShowMappingForSmoke();
            var table = Descendants(editor).OfType<TextBox>().Single(control => control.Name == "DatabaseTable");
            table.Text = "other_table";
            rejected = false;
            try { editor.CommitForSmoke(); } catch (EditorValidationException) { rejected = true; }
            Check(rejected, "changing tables did not invalidate metadata.");
            table.Text = group.Table; editor.LoadColumnsForSmoke(columns);
            VerifyEditorLayout(editor, "custom point mapping");
            var rowForLayout = grid.Rows[1];
            foreach (DataGridViewColumn column in grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible))
            {
                grid.CurrentCell = rowForLayout.Cells[column.Index];
                FocusForSmoke(grid, grid, editor, "custom point cell " + column.Name);
                grid.FirstDisplayedScrollingColumnIndex = column.Index;
                var full = grid.GetCellDisplayRectangle(column.Index, rowForLayout.Index, false);
                Check(full.Width > 0 && full == grid.GetCellDisplayRectangle(column.Index, rowForLayout.Index, true),
                    "mapping cell is clipped: " + column.Name);
            }
            grid.EndEdit();
            var savedDraft = editor.CommitForSmoke();
            editor.Close();
            using (var reopened = new DatabaseWriteGroupDialog(savedDraft, [connection], [device], _application))
            {
                reopened.Show(this); reopened.LoadColumnsForSmoke(columns); reopened.ShowMappingForSmoke();
                VerifyEditorLayout(reopened, "reopened manual mapping");
                var manualRow = reopened.SmokeMapping.Rows.Cast<DataGridViewRow>().Single(row => Equals(row.Cells["Column"].Value, "_QUALITY"));
                foreach (var column in new[] { "Include", "Source", "Constant" })
                {
                    reopened.SmokeMapping.CurrentCell = manualRow.Cells[column];
                    FocusForSmoke(reopened.SmokeMapping, reopened.SmokeMapping, reopened, "reopened manual " + column);
                    reopened.SmokeMapping.FirstDisplayedScrollingColumnIndex = manualRow.Cells[column].ColumnIndex;
                    Check(reopened.SmokeMapping.GetCellDisplayRectangle(manualRow.Cells[column].ColumnIndex, manualRow.Index, false)
                        == reopened.SmokeMapping.GetCellDisplayRectangle(manualRow.Cells[column].ColumnIndex, manualRow.Index, true), "reopened manual cell is clipped: " + column);
                }
                reopened.SmokeMapping.EndEdit(); reopened.SmokeMapping.HorizontalScrollingOffset = 0;
                manualRow.Cells["Constant"].Value = "";
                reopened.SmokeMapping.CurrentCell = manualRow.Cells["Column"];
                SaveEditorScreenshot(reopened, "recording", "manual-constant-empty");
                manualRow.Cells["Constant"].Value = "192";
                SaveEditorScreenshot(reopened, "recording", "custom-point-mapping"); reopened.Close();
            }
            AssertRecordingSmokeUnchanged(before, group, "custom point mapping source isolation");
            Console.WriteLine($"Custom point mapping smoke passed: unchecked sampling writes manual 192 as Int64; quality/integer and invalid-constant rejection; real column source labels; omitted/default and generated columns; refresh/reopen/toggle drafts; unique names, focus and target invalidation at actual {DeviceDpi} DPI; no external database contacted.");
        }
        finally { editor.Close(); }
    }

    private static string RecordingSmokeJson<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, GatewayJson.Options);
    private static void AssertRecordingSmokeUnchanged<T>(string before, T value, string context)
    { if (before != RecordingSmokeJson(value)) throw new InvalidOperationException(context + ": editor mutated the supplied configuration."); }

    private static DatabaseConnectionConfiguration RecordingConnectionFixture() => new()
    {
        Name = "采集数据库验证", Enabled = false, Provider = ExternalDatabaseProvider.PostgreSql,
        Host = "localhost", Port = 5432, Database = "edge_gateway", Username = "edge_app",
        Password = "  ui-test-password;with spaces!  ", UseTls = true,
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
                    var password = (TextBox)editor.SmokeInputs.Single(control => control.Name == "DatabasePassword");
                    Check(password.UseSystemPasswordChar, "password input is not masked.");
                    password.Text = "  literal-password;with spaces!  ";
                    Check(editor.CommitForSmoke().Password == password.Text, "password input did not preserve the literal value.");
                    var rejected = false;
                    password.Text = connection.Password;
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
                        Check(rejected && editor.Visible && password.Text == connection.Password
                            && DeviceImportSmokeJson(_configuration) == before, "failed connection save lost draft inputs or changed applied configuration.");
                        await AssertSavedConfigurationForSmokeAsync("rejected database connection save");
                    }
                    BringIntoViewAndAssert(password, "retained database password");
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
                    temperature.Cells["Source"].Value = (DatabaseValueSource)(-1); rejected = false;
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
            ((NumericUpDown)_loggingFields["MinimumFreeDiskSpaceMb"]).Value = 128;
            ((NumericUpDown)_loggingFields["DiskResumeMarginMb"]).Value = 32;
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
                && _configuration.Logging.MinimumFreeDiskSpaceMb == 128 && _configuration.Logging.DiskResumeMarginMb == 32
                && _application.IsDeviceRunning(device.Id), "logging hot apply did not persist or interrupted the device.");
            timer.Restart();
            while (!_application.GetLatest().Any(sample => sample.Timestamp > beforeSample) && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Check(_application.GetLatest().Any(sample => sample.Timestamp > beforeSample), "device did not continue producing samples after logging apply.");
            foreach (var input in _loggingFields.Values) BringIntoViewAndAssert(input, "logging settings " + input.Name);
            ResetScrollPositions(_pages["logs"], _navigation["overview"]); _logTabs.SelectedIndex = 1;
            SaveEditorScreenshot(this, "recording", "log-settings");
            _logTabs.SelectedIndex = 2;
            foreach (var control in new Control[] { _archiveFrom, _archiveTo, _archiveLevel, _archiveText, _archiveSamples,
                _archiveLimit, _archiveBudget, _archiveSearch, _archiveExport })
                BringIntoViewAndAssert(control, "log archive " + control.Name);
            await QueryArchiveAsync();
            Check(_archiveResult is not null && _archiveExport.Enabled && !_archiveCancel.Enabled, "archive query did not complete through application boundary.");
            var previewFixture = new DatabaseWritePreview(DateTimeOffset.UtcNow, true, false,
                [new("temperature", "double", "EngineeringValue", "NULL", null, "缺少活跃采样")],
                ["缺少活跃采样"], ["界面验证样例，不代表真实数据库验收。"]);
            using (var previewDialog = new DatabasePreviewDialog(previewFixture))
            {
                previewDialog.Show(this);
                try
                {
                    VerifyEditorLayout(previewDialog, "database read-only preview");
                    foreach (var tab in previewDialog.SmokeTabs)
                    { previewDialog.SelectedSmokeTab = tab; VerifyEditorLayout(previewDialog, "database preview " + tab.Text); }
                    Check(previewDialog.SmokeCommands.Count == 1, "preview unexpectedly exposes a save action.");
                }
                finally { previewDialog.Close(); }
            }
            _logTabs.SelectedIndex = 1;
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
        Select(temperature, "FieldType", DatabaseFieldType.Text);
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
            var expectedType = type == DatabaseFieldType.Auto ? DatabaseFieldType.Double : type;
            if (captured.FieldType != expectedType || captured.ConstantValue != (string)temperature.Cells["Constant"].Value)
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
