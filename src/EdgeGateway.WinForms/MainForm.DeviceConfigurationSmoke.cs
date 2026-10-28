using EdgeGateway.Abstractions;
using EdgeGateway.Application;
using EdgeGateway.Model;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeDeviceConfigurationsAsync()
    {
        if (!_smokeTest || _application.IsRunning)
            throw new InvalidOperationException("Device configuration verification requires an isolated, stopped smoke-test session.");
        var roundTrips = new HashSet<string>(StringComparer.Ordinal) { "PLC.SiemensS7", "PLC.ModBusMaster", "modbus-tcp" };
        foreach (var descriptor in _drivers.Drivers)
        {
            // Serialize the fixture through the application's real configuration format.
            // Every field starts with a recognizable saved value instead of an empty draft.
            var original = Clone(CreateConfigurationSmokeDevice(descriptor));
            var originalJson = ConfigurationSmokeJson(original);
            AssertConfigurationSmokeFixture(_drivers, original);
            DeviceConfiguration? committed = null;
            using (var editor = new DeviceConfigurationDialog(original, _drivers))
            {
                editor.Show(this);
                try
                {
                    VerifyConfigurationSmokeFields(editor, descriptor, original);
                    VerifyEditorLayout(editor, descriptor.Id + " dedicated configuration");
                    SaveEditorScreenshot(editor, "configuration", descriptor.Id);
                    if (roundTrips.Contains(descriptor.Id))
                    {
                        var expected = ChangedConfigurationSmokeDevice(original, descriptor);
                        SetConfigurationSmokeFields(editor, descriptor, expected);
                        VerifyConfigurationSmokeFields(editor, descriptor, expected);
                        committed = editor.CommitForSmoke();
                        AssertConfigurationSmokeEqual(expected, committed, descriptor.Id + " committed settings / preserved identity and tags");
                        if (ReferenceEquals(original, committed) || ReferenceEquals(original.Properties, committed.Properties)
                            || ReferenceEquals(original.Tags, committed.Tags)
                            || original.Tags.Zip(committed.Tags).Any(pair => ReferenceEquals(pair.First, pair.Second)))
                            throw new InvalidOperationException(descriptor.Id + ": dedicated configuration result shares mutable state with its source.");
                    }
                    else if (descriptor.Id == "simulator")
                    {
                        editor.SetFieldForSmoke("Enabled", "true");
                        editor.SetFieldForSmoke("Name", "不应保存的设备名称");
                        editor.SetFieldForSmoke("Group", "不应保存的分组");
                        editor.SetFieldForSmoke("PollIntervalMs", "9990");
                        var cancel = (Button)editor.CancelButton!;
                        BringIntoViewAndAssert(cancel, "cancel dedicated configuration");
                        cancel.PerformClick();
                        if (editor.Value is not null) throw new InvalidOperationException("Cancel exposed an uncommitted configuration result.");
                    }
                }
                catch { SaveEditorScreenshot(editor, "configuration-failed", descriptor.Id); throw; }
                finally { editor.Close(); }
            }

            AssertConfigurationSmokeUnchanged(originalJson, original, descriptor.Id + " original after close / commit / cancel");
            if (committed is not null)
            {
                // These three representatives cover native settings, a long parameter
                // page, and PLC enum values; avoid repeating the same edit flow 15 times.
                var saved = Clone(committed);
                using var reopened = new DeviceConfigurationDialog(saved, _drivers);
                reopened.Show(this);
                try
                {
                    VerifyConfigurationSmokeFields(reopened, descriptor, committed);
                }
                catch { TrySaveSmokeFailure(reopened, "configuration-reopen-" + descriptor.Id); throw; }
                finally { reopened.Close(); }
            }
        }

        SmokeReadOnlyDeviceConfiguration();
        SmokeInvalidDeviceConfiguration();
        await SmokeExistingDeviceDriverChangeAsync();
        await SmokeDeviceConfigurationRowActionsAsync();
        if (_application.IsRunning) throw new InvalidOperationException("Configuration smoke unexpectedly started collection.");
        Console.WriteLine($"Device configuration smoke passed: {_drivers.Drivers.Count} dedicated driver windows with saved values/layouts, "
            + "3 representative edit/reopen flows preserving identity/tags, cancel isolation, read-only view, "
            + "factory-rejected invalid station, and a non-selected-row modal apply; collection remained stopped.");
    }

    private static DeviceConfiguration CreateConfigurationSmokeDevice(DriverDescriptor descriptor)
    {
        var firstTag = TagEditor.CreateDefaultTag(descriptor, "保留主点位");
        firstTag.Unit = "smoke-unit"; firstTag.Description = "配置连接参数时保留原点位内容";
        var secondTag = Clone(firstTag);
        secondTag.Id = Guid.NewGuid(); secondTag.Name = "保留停用点位"; secondTag.Enabled = false;
        return new DeviceConfiguration
        {
            Name = "专用配置检查 · " + descriptor.Name, Group = "配置验证分组", DriverId = descriptor.Id, Enabled = false,
            PollIntervalMs = 1370, TimeoutMs = 4210, Host = "192.0.2.10", Port = 15021, UnitId = 7,
            ScanMode = DeviceScanMode.TagRate, AutoDemotionEnabled = true, DemotionFailureCount = 4, DemotionIntervalMs = 42000,
            SerialPortName = "COM17", BaudRate = 19200, DataBits = 8, Parity = "Odd", StopBits = "Two",
            Tags = [firstTag, secondTag],
            Properties = descriptor.Parameters.Where(x => x.BoundProperty is null)
                .ToDictionary(x => x.Key, x => ConfigurationSmokeParameterValue(descriptor, x, false), StringComparer.Ordinal)
        };
    }

    private static DeviceConfiguration ChangedConfigurationSmokeDevice(DeviceConfiguration source, DriverDescriptor descriptor)
    {
        var result = Clone(source);
        result.Enabled = !source.Enabled;
        result.Name = source.Name + " · 已更新";
        result.Group = "统一窗口验证分组";
        result.PollIntervalMs = 2190; result.TimeoutMs = 5430;
        result.ScanMode = DeviceScanMode.DeviceRate; result.AutoDemotionEnabled = false;
        result.DemotionFailureCount = 5; result.DemotionIntervalMs = 12500;
        if (descriptor.IsLegacy || descriptor.Parameters.Count > 0)
        {
            foreach (var parameter in descriptor.Parameters.Where(x => x.BoundProperty is null && !x.IsReadOnly))
                result.Properties[parameter.Key] = ConfigurationSmokeParameterValue(descriptor, parameter, true);
        }
        else if (descriptor.Id == "modbus-tcp")
        {
            result.Host = "192.0.2.11"; result.Port = 15022; result.UnitId = 9;
        }
        return result;
    }

    private static string ConfigurationSmokeParameterValue(DriverDescriptor descriptor, DriverParameterDescriptor parameter, bool changed)
    {
        var suffix = changed ? "11" : "10";
        return parameter.Key switch
        {
            "Ip" or "IpAddress" or "DeviceIp" => "192.0.2." + suffix,
            "Uri" => (descriptor.Id == "CNC.MTConnect" ? "http://" : "opc.tcp://") + "192.0.2." + suffix + ":5000/smoke",
            "Port" or "DevicePort" => changed ? "15022" : "15021",
            "SlaveAddress" => changed ? "9" : "7",
            "MasterType" => changed ? "Tcp" : "Rtu",
            "CpuType" when parameter.Options.Count > 1 => parameter.Options[changed ? 0 : 1],
            _ when bool.TryParse(parameter.DefaultValue, out var flag) => flag ? "true" : "false",
            _ => parameter.DefaultValue
        };
    }

    private static Dictionary<string, string> ConfigurationSmokeNativeValues(DeviceConfiguration device, DriverDescriptor descriptor)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Enabled"] = device.Enabled ? "true" : "false",
            ["Name"] = device.Name,
            ["Group"] = device.Group,
            ["PollIntervalMs"] = device.PollIntervalMs.ToString(CultureInfo.InvariantCulture),
            ["TimeoutMs"] = device.TimeoutMs.ToString(CultureInfo.InvariantCulture),
            ["ChannelId"] = device.ChannelId?.ToString() ?? "",
            ["ScanMode"] = device.ScanMode.ToString(),
            ["AutoDemotionEnabled"] = device.AutoDemotionEnabled ? "true" : "false",
            ["DemotionFailureCount"] = device.DemotionFailureCount.ToString(CultureInfo.InvariantCulture),
            ["DemotionIntervalMs"] = device.DemotionIntervalMs.ToString(CultureInfo.InvariantCulture)
        };
        if (descriptor.IsLegacy || descriptor.Parameters.Count > 0) return values;
        if (descriptor.Id == "modbus-tcp")
        {
            values["Host"] = device.Host; values["Port"] = device.Port.ToString(CultureInfo.InvariantCulture);
            values["UnitId"] = device.UnitId.ToString(CultureInfo.InvariantCulture);
        }
        else if (descriptor.Id == "modbus-rtu")
        {
            values["SerialPortName"] = device.SerialPortName; values["BaudRate"] = device.BaudRate.ToString(CultureInfo.InvariantCulture);
            values["DataBits"] = device.DataBits.ToString(CultureInfo.InvariantCulture); values["Parity"] = device.Parity;
            values["StopBits"] = device.StopBits; values["UnitId"] = device.UnitId.ToString(CultureInfo.InvariantCulture);
        }
        return values;
    }

    private static void SetConfigurationSmokeFields(DeviceConfigurationDialog editor, DriverDescriptor descriptor, DeviceConfiguration expected)
    {
        foreach (var field in ConfigurationSmokeNativeValues(expected, descriptor)) editor.SetFieldForSmoke(field.Key, field.Value);
        foreach (var parameter in descriptor.Parameters.Where(x => x.BoundProperty is null && !x.IsReadOnly))
            editor.SetFieldForSmoke("Properties." + parameter.Key, expected.Properties[parameter.Key]);
    }

    private static void VerifyConfigurationSmokeFields(DeviceConfigurationDialog editor, DriverDescriptor descriptor, DeviceConfiguration expected)
    {
        if (editor.Value is not null) throw new InvalidOperationException(descriptor.Id + ": configuration result exists before confirmation.");
        editor.VerifyBindingsForSmoke();
        var parameterPage = descriptor.IsLegacy || descriptor.Parameters.Count > 0;
        const string defaultTab = "AcquisitionSettings";
        if (editor.SelectedSmokeTab?.Name != defaultTab)
            throw new InvalidOperationException($"{descriptor.Id}: dedicated configuration opened '{editor.SelectedSmokeTab?.Name}', expected '{defaultTab}'.");
        var values = ConfigurationSmokeNativeValues(expected, descriptor);
        values["DriverId"] = expected.DriverId;
        var inputs = Descendants(editor).Where(x => x.Name.StartsWith("DeviceConfiguration_", StringComparison.Ordinal))
            .ToDictionary(x => x.Name[20..], StringComparer.Ordinal);
        if (!values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(inputs.Keys))
            throw new InvalidOperationException($"{descriptor.Id}: dedicated configuration has unrelated or missing native fields: {string.Join(", ", inputs.Keys)}.");
        foreach (var field in values)
        {
            if (field.Key == "ChannelId")
            {
                if (inputs[field.Key] is not ComboBox { SelectedIndex: >= 0 } || editor.SmokeChannelId != expected.ChannelId)
                    throw new InvalidOperationException(descriptor.Id + ": channel selection lost its saved stable identity.");
            }
            else AssertConfigurationSmokeInput(inputs[field.Key], field.Value, descriptor.Id);
        }
        if (parameterPage)
        {
            // The shared panel already verifies bound values and enum options. Here
            // verify the new dialog actually loaded this device's saved properties.
            foreach (var parameter in descriptor.Parameters.Where(x => x.BoundProperty is null))
                AssertConfigurationSmokeInput(editor.SmokeParameterInputs.Single(x => x.Name == "DriverParameter_" + parameter.Key),
                    expected.Properties.GetValueOrDefault(parameter.Key, parameter.DefaultValue), descriptor.Id);
        }
        else if (editor.SmokeParameterInputs.Count != values.Count - 11)
            throw new InvalidOperationException(descriptor.Id + ": native connection field coverage differs from the displayed driver.");
    }

    private static void AssertConfigurationSmokeInput(Control control, string expected, string context)
    {
        var actual = control switch
        {
            NumericUpDown number => number.Value.ToString(CultureInfo.InvariantCulture),
            CheckBox toggle => toggle.Checked ? "true" : "false",
            ComboBox { SelectedItem: DriverDescriptor descriptor } => descriptor.Id,
            ComboBox { DropDownStyle: ComboBoxStyle.DropDown } choice => choice.Text,
            ComboBox choice => choice.SelectedItem?.ToString() ?? "",
            _ => control.Text
        };
        if (actual != expected)
            throw new InvalidOperationException($"{context}: {control.GetType().Name} '{control.Name}' displays '{actual}', expected saved value '{expected}'.");
    }

    private static string ConfigurationSmokeJson(DeviceConfiguration configuration) =>
        JsonSerializer.Serialize(configuration, GatewayJson.Options);

    private static void AssertConfigurationSmokeEqual(DeviceConfiguration expected, DeviceConfiguration actual, string context) =>
        AssertConfigurationSmokeUnchanged(ConfigurationSmokeJson(expected), actual, context);

    private static void AssertConfigurationSmokeUnchanged(string expectedJson, DeviceConfiguration actual, string context)
    {
        if (ConfigurationSmokeJson(actual) != expectedJson)
            throw new InvalidOperationException(context + ": device identity, tags, or configuration values changed unexpectedly.");
    }

    private static void AssertConfigurationSmokeFixture(IDriverCatalog catalog, DeviceConfiguration device)
    {
        var errors = catalog.Validate(device);
        if (errors.Count > 0) throw new InvalidOperationException(catalog.GetDescriptor(device.DriverId).Id + " configuration smoke fixture is invalid: " + string.Join("; ", errors));
    }

    private void SmokeReadOnlyDeviceConfiguration()
    {
        var descriptor = _drivers.GetDescriptor("PLC.ModBusMaster");
        var device = CreateConfigurationSmokeDevice(descriptor);
        var before = ConfigurationSmokeJson(device);
        using var editor = new DeviceConfigurationDialog(device, _drivers, readOnly: true);
        editor.Show(this);
        try
        {
            VerifyConfigurationSmokeFields(editor, descriptor, device);
            VerifyEditorLayout(editor, "read-only dedicated configuration");
            if (editor.SmokeCommands.Count != 1 || editor.SmokeCommands[0].Text != "关闭")
                throw new InvalidOperationException("Read-only configuration exposes an apply command.");
            var rejected = false;
            try { editor.CommitForSmoke(); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || editor.Value is not null) throw new InvalidOperationException("Read-only configuration accepted an apply operation.");
            AssertConfigurationSmokeUnchanged(before, device, "read-only configuration");
        }
        catch { TrySaveSmokeFailure(editor, "configuration-readonly-failed"); throw; }
        finally { editor.Close(); }
    }

    private void SmokeInvalidDeviceConfiguration()
    {
        var descriptor = _drivers.GetDescriptor("PLC.ModBusMaster");
        var original = CreateConfigurationSmokeDevice(descriptor);
        var before = ConfigurationSmokeJson(original);
        var invalid = Clone(original);
        invalid.Properties["SlaveAddress"] = "0";
        using var editor = new DeviceConfigurationDialog(original, _drivers);
        editor.Show(this);
        try
        {
            // Zero fits the reflected byte input, but the actual protocol factory
            // must reject a broadcast station rather than accepting an unsafe draft.
            editor.SetFieldForSmoke("Properties.SlaveAddress", "0");
            AssertConfigurationFactoryRejection(editor, _drivers, invalid);
            AssertConfigurationSmokeUnchanged(before, original, "rejected station edit");
        }
        catch { TrySaveSmokeFailure(editor, "configuration-invalid-station-failed"); throw; }
        finally { editor.Close(); }
    }

    private static void AssertConfigurationFactoryRejection(DeviceConfigurationDialog editor, IDriverCatalog catalog, DeviceConfiguration invalid)
    {
        var candidate = Clone(invalid); candidate.Tags = [];
        var expectedErrors = catalog.Validate(candidate);
        if (expectedErrors.Count == 0) throw new InvalidOperationException(invalid.DriverId + ": invalid fixture was accepted by its driver factory.");
        Exception? rejection = null;
        try { editor.CommitForSmoke(); }
        catch (InvalidOperationException exception) { rejection = exception; }
        if (rejection is null || editor.Value is not null || expectedErrors.Any(error => !rejection.Message.Contains(error, StringComparison.Ordinal)))
            throw new InvalidOperationException(invalid.DriverId + ": configuration dialog did not preserve the actual factory rejection: " + string.Join("; ", expectedErrors), rejection);
    }

    private async Task SmokeExistingDeviceDriverChangeAsync()
    {
        var originalConfiguration = _configuration;
        var originalDirty = _dirty;
        var originalSelection = SelectedDevice()?.Id;
        try
        {
            var device = CreateConfigurationSmokeDevice(_drivers.GetDescriptor("simulator"));
            device.Tags[0].Address = "ramp";
            var fixture = Clone(_configuration); fixture.Devices = [device];
            await CommitConfigurationAsync(fixture, device.Id);
            var before = DeviceImportSmokeJson(_configuration);
            var source = _configuration.Devices.Single();
            var sourceJson = ConfigurationSmokeJson(source);
            using var editor = new DeviceConfigurationDialog(source, _drivers);
            editor.Show(this);
            try
            {
                editor.SelectDriverForSmoke("modbus-tcp");
                editor.SetFieldForSmoke("Host", "192.0.2.44");
                var changed = editor.CommitForSmoke();
                if (changed.Id != source.Id || changed.DriverId != "modbus-tcp"
                    || JsonSerializer.Serialize(changed.Tags, GatewayJson.Options)
                        != JsonSerializer.Serialize(source.Tags, GatewayJson.Options))
                    throw new InvalidOperationException("Changing an existing device driver replaced its identity or points.");
                var candidate = Clone(_configuration); candidate.Devices[0] = changed;
                var rejected = false;
                try { await CommitConfigurationAsync(candidate, source.Id); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || DeviceImportSmokeJson(_configuration) != before)
                    throw new InvalidOperationException("An incompatible driver change was saved or changed the current configuration.");
                AssertConfigurationSmokeUnchanged(sourceJson, source, "existing driver change source");
                await AssertSavedConfigurationForSmokeAsync("rejected incompatible driver change");
                Console.WriteLine("Existing device driver change smoke passed: identity and points preserved, incompatible addresses rejected by full configuration validation, saved data unchanged.");
            }
            catch { TrySaveSmokeFailure(editor, "existing-driver-change-failed"); throw; }
            finally { editor.Close(); }
        }
        finally
        {
            await RestoreConfigurationForSmokeAsync(originalConfiguration); _dirty = originalDirty;
            RebindConfigurationViews(originalSelection); UpdateCommands();
        }
    }

    private async Task SmokeDeviceConfigurationRowActionsAsync()
    {
        var originalConfiguration = _configuration;
        var originalDirty = _dirty;
        var originalPage = _activePage;
        var originalSelection = SelectedDevice()?.Id;
        var originalFooter = _lastFeedback;
        var descriptor = _drivers.GetDescriptor("modbus-tcp");
        var selected = CreateConfigurationSmokeDevice(descriptor); selected.Name = "选中设备 A"; selected.Host = "192.0.2.31";
        var target = CreateConfigurationSmokeDevice(descriptor); target.Name = "点击配置设备 B"; target.Host = "192.0.2.32";
        var selectedJson = ConfigurationSmokeJson(selected);
        var targetJson = ConfigurationSmokeJson(target);
        try
        {
            var fixture = Clone(originalConfiguration);
            // Build fixtures through the same application transaction as production.
            // B precedes A so current selection cannot redirect B's row operation.
            fixture.Devices = [target, selected];
            await CommitConfigurationAsync(fixture, selected.Id, replaceCloudDraft: true);
            ShowPage("devices");
            var column = _deviceGrid.Columns[DeviceConfigurationColumnName];
            if (column is not GatewayTableLinkColumn { HeaderText: "配置", Text: "配置" }
                || column.DisplayIndex != 3 || _deviceGrid.Columns[DeviceStartColumnName].DisplayIndex != 1
                || _deviceGrid.Columns[DeviceStopColumnName].DisplayIndex != 2)
                throw new InvalidOperationException("Device row commands must immediately follow the device name and retain their Chinese captions.");
            await SmokeDeviceConfigurationRowActionAsync(target, enabled: true);
            var expected = Clone(target); expected.Port = 15029; expected.Enabled = true;
            AssertConfigurationSmokeEqual(expected, _configuration.Devices.Single(x => x.Id == target.Id), "applied non-selected device row");
            AssertSavedDeviceEnabledRowForSmoke(target.Id, true);
            await AssertSavedConfigurationForSmokeAsync("enabled device row configuration");
            // Double-click a normal data cell while A is selected: it must use
            // the very same dedicated configuration dialog and row identity.
            RebindConfigurationViews(selected.Id);
            await SmokeDeviceConfigurationRowActionAsync(expected, doubleClick: true, port: 15030, enabled: false);
            expected.Port = 15030; expected.Enabled = false;
            AssertConfigurationSmokeEqual(expected, _configuration.Devices.Single(x => x.Id == target.Id), "double-clicked non-selected device row");
            AssertSavedDeviceEnabledRowForSmoke(target.Id, false);
            await SmokeDeviceConfigurationRowActionAsync(expected, toolbarEdit: true, port: 15031, enabled: true);
            expected.Port = 15031; expected.Enabled = true;
            AssertConfigurationSmokeEqual(expected, _configuration.Devices.Single(x => x.Id == target.Id), "toolbar edited device");
            AssertSavedDeviceEnabledRowForSmoke(target.Id, true);
            AssertConfigurationSmokeUnchanged(targetJson, target, "row action source device");
            AssertConfigurationSmokeUnchanged(selectedJson, _configuration.Devices.Single(x => x.Id == selected.Id), "other selected device row");
            if (_dirty || SelectedDevice()?.Id != target.Id)
                throw new InvalidOperationException("Applied row configuration was not automatically saved or failed to preserve the target selection.");
            await AssertSavedConfigurationForSmokeAsync("device row configuration");
            Console.WriteLine("Device row entry smoke passed: configuration link and data-cell double-click opened the same dedicated dialog for clicked B while A was selected, and both automatically saved.");
            Console.WriteLine("Device enabled configuration smoke passed: enabled via configuration link, disabled via row double-click, saved JSON and row start availability matched; cancelled edits stayed isolated.");
            var added = await SmokeDeviceConfigurationModalAsync(AddDeviceAsync, dialog =>
            {
                if (dialog.Value is not null || dialog.SelectedSmokeTab?.Name != "AcquisitionSettings")
                    throw new InvalidOperationException("Unified add dialog has an unexpected initial result or page.");
                dialog.SelectDriverForSmoke("modbus-tcp");
                dialog.SetFieldForSmoke("Enabled", "false");
                dialog.SetFieldForSmoke("Name", "统一窗口新增设备 C");
                dialog.SetFieldForSmoke("Group", "统一窗口新增分组");
                dialog.SetFieldForSmoke("Host", "192.0.2.33");
                dialog.SetFieldForSmoke("Port", "15032");
                dialog.VerifyBindingsForSmoke();
                VerifyEditorLayout(dialog, "unified add device modal");
            }, "add device toolbar");
            if (_configuration.Devices.Count != 3 || added.DriverId != "modbus-tcp" || added.Tags.Count != 1
                || added.Name != "统一窗口新增设备 C" || added.Group != "统一窗口新增分组"
                || added.Enabled || SelectedDevice()?.Id != added.Id)
                throw new InvalidOperationException("Unified add entry did not save the selected driver, identity, default point or device selection.");
            AssertConfigurationSmokeEqual(added, _configuration.Devices.Single(x => x.Id == added.Id), "added unified device");
            AssertConfigurationSmokeEqual(expected, _configuration.Devices.Single(x => x.Id == target.Id), "existing device after add");
            AssertConfigurationSmokeUnchanged(selectedJson, _configuration.Devices.Single(x => x.Id == selected.Id), "selected device after add");
            await AssertSavedConfigurationForSmokeAsync("unified add device");
            Console.WriteLine("Unified device dialog smoke passed: add, toolbar edit, configuration link and row double-click all used DeviceConfigurationDialog and saved through the same configuration pipeline.");
        }
        finally
        {
            await RestoreConfigurationForSmokeAsync(originalConfiguration); _dirty = originalDirty;
            RebindConfigurationViews(originalSelection); UpdateCommands(); ShowPage(originalPage); SetFooter(originalFooter);
        }
    }

    private void AssertSavedDeviceEnabledRowForSmoke(Guid deviceId, bool enabled)
    {
        var row = _deviceGrid.Rows.Cast<GatewayTableRow>().Single(x => x.DataBoundItem is DeviceRow device && device.Id == deviceId);
        if (row.DataBoundItem is not DeviceRow data || data.Enabled != (enabled ? "是" : "否")
            || ((DeviceActionButtonCell)row.Cells[DeviceStartColumnName]).ActionEnabled != enabled
            || _application.IsRunning)
            throw new InvalidOperationException("Saved Enabled state did not update the device row/start command, or unexpectedly started collection.");
    }

    private async Task SmokeDeviceConfigurationRowActionAsync(DeviceConfiguration target, bool doubleClick = false, int port = 15029, bool? enabled = null, bool toolbarEdit = false)
    {
        if (toolbarEdit) RebindConfigurationViews(target.Id);
        else if (SelectedDevice()?.Id == target.Id) throw new InvalidOperationException("Row-action smoke must start with another device selected.");
        var row = _deviceGrid.Rows.Cast<GatewayTableRow>().Single(x => x.DataBoundItem is DeviceRow device && device.Id == target.Id);
        var column = _deviceGrid.Columns[doubleClick ? "Name" : DeviceConfigurationColumnName];
        await SmokeDeviceConfigurationModalAsync(
            toolbarEdit ? EditDeviceAsync : () => DeviceGridActionAsync(new DataGridViewCellEventArgs(column.Index, row.Index), doubleClick),
            dialog =>
            {
                VerifyConfigurationSmokeFields(dialog, _drivers.GetDescriptor(target.DriverId), target);
                if (enabled.HasValue) dialog.SetFieldForSmoke("Enabled", enabled.Value ? "true" : "false");
                dialog.SetFieldForSmoke("Port", port.ToString(CultureInfo.InvariantCulture));
            }, toolbarEdit ? "edit device toolbar" : "non-selected device row");
    }

    private async Task<DeviceConfiguration> SmokeDeviceConfigurationModalAsync(Func<Task> open, Action<DeviceConfigurationDialog> edit, string context)
    {
        var observed = false;
        DeviceConfiguration? confirmed = null;
        Exception? failure = null;
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += async (_, _) =>
        {
            var dialog = System.Windows.Forms.Application.OpenForms.OfType<DeviceConfigurationDialog>().SingleOrDefault(x => x.Visible && x.Modal);
            if (dialog is null)
            {
                if (elapsed.Elapsed < TimeSpan.FromSeconds(5)) return;
                failure = new InvalidOperationException(context + ": unified device configuration did not open within 5 seconds.");
                timer.Stop();
                foreach (var modal in System.Windows.Forms.Application.OpenForms.Cast<Form>().Where(x => x.Modal).ToArray()) modal.Close();
                return;
            }
            timer.Stop(); observed = true;
            try
            {
                edit(dialog);
                // Exercise the same awaited validate/save/apply path as the real confirmation button.
                await dialog.ApplyAndAcceptForSmokeAsync();
                confirmed = dialog.Value;
            }
            catch (Exception exception)
            {
                failure = exception;
                TrySaveSmokeFailure(dialog, "configuration-row-action-failed");
                dialog.DialogResult = DialogResult.Cancel;
            }
            finally { dialog.Close(); }
        };
        timer.Start();
        try
        {
            await open();
            // AntdUI menus dispatch after their close animation on the UI queue.
            // Wait for the actual modal result rather than a fixed caller delay.
            while (confirmed is null && failure is null && elapsed.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(20);
        }
        finally { timer.Stop(); }
        if (failure is not null) throw new InvalidOperationException(context + ": unified device configuration failed.", failure);
        if (!observed || confirmed is null) throw new InvalidOperationException(context + $": expected device dialog was not confirmed; observed={observed}, elapsed={elapsed.Elapsed}, forms={string.Join(",", System.Windows.Forms.Application.OpenForms.Cast<Form>().Select(f => f.GetType().Name + "/" + f.Visible + "/" + f.Modal))}.");
        return confirmed;
    }

    private void VerifyMinimumConfigurationDialog(Rectangle area, string directory, LayoutCaseResult result)
    {
        var descriptor = _drivers.GetDescriptor("PLC.ModBusMaster");
        var device = CreateConfigurationSmokeDevice(descriptor);
        using var editor = new DeviceConfigurationDialog(device, _drivers);
        editor.Show(this);
        try
        {
            FitSmokeDialog(editor, area);
            VerifyConfigurationSmokeFields(editor, descriptor, device);
            VerifyEditorLayout(editor, result.Name + " dedicated configuration");
            foreach (var command in editor.SmokeCommands) BringIntoViewAndAssert(command, result.Name + " configuration action");
            result.ConfigurationParameterMetrics = editor.SmokeParameterInputs.Select(ControlMetric.From).ToList();
            SaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-modbus-configuration.png"));
            result.CheckedDialogs.Add("PLC.ModBusMaster dedicated configuration");
        }
        catch { TrySaveSmokeScreenshot(editor, Path.Combine(directory, result.Name + "-configuration-failed.png")); throw; }
        finally { editor.Close(); }
    }
}
