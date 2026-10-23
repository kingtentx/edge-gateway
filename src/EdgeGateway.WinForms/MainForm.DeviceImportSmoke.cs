using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private void SmokeDeviceImport()
    {
        if (!_smokeTest || _runtime.IsRunning)
            throw new InvalidOperationException("Device import verification requires an isolated, stopped smoke-test session.");
        var original = _configuration;
        var originalDirty = _dirty;
        var originalSelection = SelectedDevice()?.Id;
        var originalPage = _activePage;
        var originalFooter = _footer.Text;
        try
        {
            var incoming = Clone(new GatewayConfiguration { Devices = [DemoConfiguration.CreateModbusRtuExample()] });
            var source = incoming.Devices.Single();
            // Represent an existing installation with its own settings and devices.
            _configuration = Clone(original);
            _configuration.Devices.RemoveAll(x => x.Id == source.Id);
            _configuration.GatewayId = "device-import-smoke";
            _configuration.Name = "保留当前网关";
            _configuration.HistoryRetentionDays = 17;
            _configuration.Mqtt.Host = "existing-broker.invalid";
            var before = DeviceImportSmokeJson(_configuration);
            AppendImportedDevices(incoming);

            var added = _configuration.Devices.Single(x => x.Id == source.Id);
            var preserved = Clone(_configuration);
            preserved.Devices.RemoveAll(x => x.Id == source.Id);
            if (DeviceImportSmokeJson(preserved) != before || !_dirty || SelectedDevice()?.Id != source.Id)
                throw new InvalidOperationException("Appending devices changed existing configuration or failed to select the imported draft.");
            AssertConfigurationSmokeEqual(source, added, "appended RTU device");
            if (ReferenceEquals(source, added) || ReferenceEquals(source.Properties, added.Properties)
                || ReferenceEquals(source.Tags, added.Tags)
                || source.Tags.Zip(added.Tags).Any(pair => ReferenceEquals(pair.First, pair.Second)))
                throw new InvalidOperationException("Imported device shares mutable state with the input configuration.");

            AssertDeviceImportRejected(incoming, "duplicate identities");
            // Use fresh identities to ensure the second rejection is a serial conflict.
            var conflicting = Clone(incoming);
            conflicting.Devices[0].Id = Guid.NewGuid();
            foreach (var tag in conflicting.Devices[0].Tags) tag.Id = Guid.NewGuid();
            added.Enabled = true;
            conflicting.Devices[0].Enabled = true;
            AssertDeviceImportRejected(conflicting, "enabled serial-port conflict");
            added.Enabled = false;

            ShowPage("devices");
            RebindConfigurationViews(added.Id);
            PerformLayoutRecursively(this);
            AssertPageToolbarsFit(_pages["devices"], "device import");
            if (_tagGrid.Rows.Count != added.Tags.Count)
                throw new InvalidOperationException("Imported RTU points are missing from the selected device grid.");
            SaveEditorScreenshot(this, "preset", "modbus-rtu-com11");
            var factory = _drivers.GetFactory(added.DriverId);
            using var editor = new DeviceConfigurationDialog(added, factory);
            editor.Show(this);
            try
            {
                VerifyConfigurationSmokeFields(editor, factory.Descriptor, added);
                VerifyEditorLayout(editor, "imported RTU COM11 preset");
                SaveEditorScreenshot(editor, "preset-configuration", "modbus-rtu-com11");
            }
            catch { SaveEditorScreenshot(editor, "preset-configuration-failed", "modbus-rtu-com11"); throw; }
            finally { editor.Close(); }

            if (_runtime.IsRunning) throw new InvalidOperationException("Device import smoke unexpectedly started collection.");
            Console.WriteLine("Device import smoke passed: append preserves existing gateway/platform/devices, deep-copy isolation, "
                + "duplicate identities and enabled serial conflicts rejected without mutation, imported RTU points and COM11 configuration displayed; collection remained stopped.");
        }
        finally
        {
            _configuration = original;
            _dirty = originalDirty;
            RebindConfigurationViews(originalSelection);
            ShowPage(originalPage);
            SetFooter(originalFooter);
            UpdateCommands();
        }
    }

    private void AssertDeviceImportRejected(GatewayConfiguration incoming, string context)
    {
        var before = DeviceImportSmokeJson(_configuration);
        var rejected = false;
        try { AppendImportedDevices(incoming); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected || DeviceImportSmokeJson(_configuration) != before)
            throw new InvalidOperationException(context + ": invalid device import was accepted or changed the current configuration.");
    }

    private static string DeviceImportSmokeJson(GatewayConfiguration configuration) =>
        JsonSerializer.Serialize(configuration, JsonConfigurationStore.SerializerOptions);
}
