using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private void SmokeDeviceConfigurations()
    {
        if (!_smokeTest || _runtime.IsRunning)
            throw new InvalidOperationException("Device configuration verification requires an isolated, stopped smoke-test session.");
        var roundTrips = new HashSet<string>(StringComparer.Ordinal) { "PLC.SiemensS7", "PLC.ModBusMaster", "modbus-tcp" };
        foreach (var descriptor in _drivers.Drivers)
        {
            var factory = _drivers.GetFactory(descriptor.Id);
            // Serialize the fixture through the application's real configuration format.
            // Every field starts with a recognizable saved value instead of an empty draft.
            var original = Clone(CreateConfigurationSmokeDevice(descriptor));
            var originalJson = ConfigurationSmokeJson(original);
            AssertConfigurationSmokeFixture(factory, original);
            DeviceConfiguration? committed = null;
            using (var editor = new DeviceConfigurationDialog(original, factory))
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
                using var reopened = new DeviceConfigurationDialog(saved, factory);
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
        SmokeDeviceConfigurationRowActions();
        if (_runtime.IsRunning) throw new InvalidOperationException("Configuration smoke unexpectedly started collection.");
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
            SerialPortName = "COM17", BaudRate = 19200, DataBits = 8, Parity = "Odd", StopBits = "Two",
            Tags = [firstTag, secondTag],
            Properties = descriptor.Parameters.Where(x => x.BoundProperty is null)
                .ToDictionary(x => x.Key, x => ConfigurationSmokeParameterValue(descriptor, x, false), StringComparer.Ordinal)
        };
    }

    private static DeviceConfiguration ChangedConfigurationSmokeDevice(DeviceConfiguration source, DriverDescriptor descriptor)
    {
        var result = Clone(source);
        result.PollIntervalMs = 2190; result.TimeoutMs = 5430;
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
            ["PollIntervalMs"] = device.PollIntervalMs.ToString(CultureInfo.InvariantCulture),
            ["TimeoutMs"] = device.TimeoutMs.ToString(CultureInfo.InvariantCulture)
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
        var defaultTab = parameterPage ? "DriverConfigurationParameters"
            : descriptor.Id is "modbus-tcp" or "modbus-rtu" ? "NativeConnection" : "AcquisitionSettings";
        if (editor.SelectedSmokeTab?.Name != defaultTab)
            throw new InvalidOperationException($"{descriptor.Id}: dedicated configuration opened '{editor.SelectedSmokeTab?.Name}', expected '{defaultTab}'.");
        var values = ConfigurationSmokeNativeValues(expected, descriptor);
        values["Name"] = expected.Name; values["DriverId"] = descriptor.Name + " · " + expected.DriverId;
        var inputs = Descendants(editor).Where(x => x.Name.StartsWith("DeviceConfiguration_", StringComparison.Ordinal))
            .ToDictionary(x => x.Name[20..], StringComparer.Ordinal);
        if (!values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(inputs.Keys))
            throw new InvalidOperationException($"{descriptor.Id}: dedicated configuration has unrelated or missing native fields: {string.Join(", ", inputs.Keys)}.");
        foreach (var field in values) AssertConfigurationSmokeInput(inputs[field.Key], field.Value, descriptor.Id);
        if (parameterPage)
        {
            // The shared panel already verifies bound values and enum options. Here
            // verify the new dialog actually loaded this device's saved properties.
            foreach (var parameter in descriptor.Parameters.Where(x => x.BoundProperty is null))
                AssertConfigurationSmokeInput(editor.SmokeParameterInputs.Single(x => x.Name == "DriverParameter_" + parameter.Key),
                    expected.Properties.GetValueOrDefault(parameter.Key, parameter.DefaultValue), descriptor.Id);
        }
        else if (editor.SmokeParameterInputs.Count != values.Count - 4)
            throw new InvalidOperationException(descriptor.Id + ": native connection field coverage differs from the displayed driver.");
    }

    private static void AssertConfigurationSmokeInput(Control control, string expected, string context)
    {
        var actual = control switch
        {
            NumericUpDown number => number.Value.ToString(CultureInfo.InvariantCulture),
            CheckBox toggle => toggle.Checked ? "true" : "false",
            ComboBox choice => choice.SelectedItem?.ToString() ?? "",
            _ => control.Text
        };
        if (actual != expected)
            throw new InvalidOperationException($"{context}: {control.GetType().Name} '{control.Name}' displays '{actual}', expected saved value '{expected}'.");
    }

    private static string ConfigurationSmokeJson(DeviceConfiguration configuration) =>
        JsonSerializer.Serialize(configuration, JsonConfigurationStore.SerializerOptions);

    private static void AssertConfigurationSmokeEqual(DeviceConfiguration expected, DeviceConfiguration actual, string context) =>
        AssertConfigurationSmokeUnchanged(ConfigurationSmokeJson(expected), actual, context);

    private static void AssertConfigurationSmokeUnchanged(string expectedJson, DeviceConfiguration actual, string context)
    {
        if (ConfigurationSmokeJson(actual) != expectedJson)
            throw new InvalidOperationException(context + ": device identity, tags, or configuration values changed unexpectedly.");
    }

    private static void AssertConfigurationSmokeFixture(IDriverFactory factory, DeviceConfiguration device)
    {
        var errors = factory.Validate(device);
        if (errors.Count > 0) throw new InvalidOperationException(factory.Descriptor.Id + " configuration smoke fixture is invalid: " + string.Join("; ", errors));
    }

    private void SmokeReadOnlyDeviceConfiguration()
    {
        var factory = _drivers.GetFactory("PLC.ModBusMaster");
        var device = CreateConfigurationSmokeDevice(factory.Descriptor);
        var before = ConfigurationSmokeJson(device);
        using var editor = new DeviceConfigurationDialog(device, factory, readOnly: true);
        editor.Show(this);
        try
        {
            VerifyConfigurationSmokeFields(editor, factory.Descriptor, device);
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
        var factory = _drivers.GetFactory("PLC.ModBusMaster");
        var original = CreateConfigurationSmokeDevice(factory.Descriptor);
        var before = ConfigurationSmokeJson(original);
        var invalid = Clone(original);
        invalid.Properties["SlaveAddress"] = "0";
        using var editor = new DeviceConfigurationDialog(original, factory);
        editor.Show(this);
        try
        {
            // Zero fits the reflected byte input, but the actual protocol factory
            // must reject a broadcast station rather than accepting an unsafe draft.
            editor.SetFieldForSmoke("Properties.SlaveAddress", "0");
            AssertConfigurationFactoryRejection(editor, factory, invalid);
            AssertConfigurationSmokeUnchanged(before, original, "rejected station edit");
        }
        catch { TrySaveSmokeFailure(editor, "configuration-invalid-station-failed"); throw; }
        finally { editor.Close(); }
    }

    private static void AssertConfigurationFactoryRejection(DeviceConfigurationDialog editor, IDriverFactory factory, DeviceConfiguration invalid)
    {
        var candidate = Clone(invalid); candidate.Tags = [];
        var expectedErrors = factory.Validate(candidate);
        if (expectedErrors.Count == 0) throw new InvalidOperationException(factory.Descriptor.Id + ": invalid fixture was accepted by its driver factory.");
        Exception? rejection = null;
        try { editor.CommitForSmoke(); }
        catch (InvalidOperationException exception) { rejection = exception; }
        if (rejection is null || editor.Value is not null || expectedErrors.Any(error => !rejection.Message.Contains(error, StringComparison.Ordinal)))
            throw new InvalidOperationException(factory.Descriptor.Id + ": configuration dialog did not preserve the actual factory rejection: " + string.Join("; ", expectedErrors), rejection);
    }

    private void SmokeDeviceConfigurationRowActions()
    {
        var originalConfiguration = _configuration;
        var originalDirty = _dirty;
        var originalPage = _activePage;
        var originalSelection = SelectedDevice()?.Id;
        var originalFooter = _footer.Text;
        var descriptor = _drivers.GetFactory("modbus-tcp").Descriptor;
        var selected = CreateConfigurationSmokeDevice(descriptor); selected.Name = "选中设备 A"; selected.Host = "192.0.2.31";
        var target = CreateConfigurationSmokeDevice(descriptor); target.Name = "点击配置设备 B"; target.Host = "192.0.2.32";
        var selectedJson = ConfigurationSmokeJson(selected);
        var targetJson = ConfigurationSmokeJson(target);
        try
        {
            _configuration = Clone(originalConfiguration);
            // Deliberately place B before A: neither CurrentRow nor a remembered list
            // index may redirect a row action intended for B to the selected device A.
            _configuration.Devices = [target, selected];
            _dirty = false; RebindConfigurationViews(selected.Id); UpdateCommands(); ShowPage("devices");
            var column = _deviceGrid.Columns[DeviceConfigurationColumnName];
            if (column is not DataGridViewLinkColumn || column.DisplayIndex != _deviceGrid.Columns.Count - 1)
                throw new InvalidOperationException("Device configuration action is missing from the rightmost device column.");
            SmokeDeviceConfigurationRowAction(target);
            var expected = Clone(target); expected.Port = 15029;
            AssertConfigurationSmokeEqual(expected, _configuration.Devices.Single(x => x.Id == target.Id), "applied non-selected device row");
            AssertConfigurationSmokeUnchanged(targetJson, target, "row action source device");
            AssertConfigurationSmokeUnchanged(selectedJson, _configuration.Devices.Single(x => x.Id == selected.Id), "other selected device row");
            if (!_dirty || SelectedDevice()?.Id != target.Id)
                throw new InvalidOperationException("Applied row configuration did not mark the draft dirty or preserve the target selection.");
        }
        finally
        {
            _configuration = originalConfiguration; _dirty = originalDirty;
            RebindConfigurationViews(originalSelection); UpdateCommands(); ShowPage(originalPage); SetFooter(originalFooter);
        }
    }

    private void SmokeDeviceConfigurationRowAction(DeviceConfiguration target)
    {
        if (SelectedDevice()?.Id == target.Id) throw new InvalidOperationException("Row-action smoke must start with another device selected.");
        var row = _deviceGrid.Rows.Cast<DataGridViewRow>().Single(x => x.DataBoundItem is DeviceRow device && device.Id == target.Id);
        var column = _deviceGrid.Columns[DeviceConfigurationColumnName];
        var observed = false;
        Exception? failure = null;
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.OfType<DeviceConfigurationDialog>().SingleOrDefault(x => x.Visible && x.Modal);
            if (dialog is null)
            {
                if (elapsed.Elapsed < TimeSpan.FromSeconds(5)) return;
                failure = new InvalidOperationException("Device configuration row action did not open its modal window within 5 seconds.");
                timer.Stop();
                foreach (var modal in Application.OpenForms.Cast<Form>().Where(x => x.Modal).ToArray()) modal.Close();
                return;
            }
            timer.Stop(); observed = true;
            try
            {
                VerifyConfigurationSmokeFields(dialog, _drivers.GetFactory(target.DriverId).Descriptor, target);
                dialog.SetFieldForSmoke("Port", "15029");
                // Exercise the production Commit path and ShowDialog's OK result
                // without leaving an unexpected validation MessageBox blocking CI.
                dialog.CommitForSmoke();
                dialog.DialogResult = DialogResult.OK;
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
        try { DeviceConfigurationCellContentClick(_deviceGrid, new DataGridViewCellEventArgs(column.Index, row.Index)); }
        finally { timer.Stop(); }
        if (failure is not null) throw new InvalidOperationException("Non-selected device configuration row action failed.", failure);
        if (!observed) throw new InvalidOperationException("Device configuration cell action did not show the expected modal dialog.");
    }

    private void VerifyMinimumConfigurationDialog(Rectangle area, string directory, LayoutCaseResult result)
    {
        var factory = _drivers.GetFactory("PLC.ModBusMaster");
        var device = CreateConfigurationSmokeDevice(factory.Descriptor);
        using var editor = new DeviceConfigurationDialog(device, factory);
        editor.Show(this);
        try
        {
            FitSmokeDialog(editor, area);
            VerifyConfigurationSmokeFields(editor, factory.Descriptor, device);
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
