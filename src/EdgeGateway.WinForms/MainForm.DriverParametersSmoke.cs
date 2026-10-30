using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool DriverParametersSmoke { get; set; }
    private async Task SmokeDriverParametersAsync()
    {
        _refreshTimer.Stop();
        foreach (var descriptor in _drivers.Drivers)
        {
            var device = CreateConfigurationSmokeDevice(descriptor);
            using var editor = new DeviceConfigurationDialog(device, _drivers);
            editor.Show(this);
            try
            {
                VerifyConfigurationSmokeFields(editor, descriptor, device);
                var result = editor.CommitForSmoke();
                AssertConfigurationSmokeEqual(device, result, descriptor.Id + " all parameter values");
                var wide = editor.Size;
                foreach (var narrow in new[] { false, true })
                {
                    if (narrow) editor.Size = new Size(Math.Max(editor.MinimumSize.Width, UiTheme.Scale(editor, 680)), wide.Height);
                    foreach (var tab in editor.SmokeTabs)
                    {
                        editor.SelectedSmokeTab = tab;
                        VerifyEditorLayout(editor, descriptor.Id + " unified parameters");
                    }
                    editor.SelectedSmokeTab = editor.SmokeTabs.Single(tab => tab.Name == "DriverConfigurationParameters");
                    await Task.Delay(200);
                    SaveEditorScreenshot(editor, "driver-parameters", descriptor.Id + (narrow ? "-narrow" : "-wide"));
                }
            }
            finally { editor.Close(); }
        }
        var tcp = new DeviceConfiguration { DriverId = "modbus-tcp", Name = "切换检查", Host = "192.0.2.20", Properties = new() { ["Extra"] = "draft" } };
        using var switching = new DeviceConfigurationDialog(tcp, _drivers);
        switching.Show(this);
        var custom = Descendants(switching).OfType<DriverCustomParametersView>().Single();
        switching.SelectedSmokeTab = switching.SmokeTabs.Single(tab => tab.Controls.Contains(custom));
        custom.VerifyEditingForSmoke();
        custom.LoadValues(new Dictionary<string, string> { ["Endpoint"] = "fixture://设备", ["Token"] = "fake-token" }, [], false);
        VerifyEditorLayout(switching, "custom parameters");
        await Task.Delay(200);
        SaveEditorScreenshot(switching, "driver-parameters", "custom");
        custom.LoadValues(tcp.Properties, _drivers.GetDescriptor("modbus-tcp").Parameters.Select(p => p.Key), false);
        switching.SetFieldForSmoke("Host", "192.0.2.21");
        switching.SelectDriverForSmoke("simulator");
        switching.SelectDriverForSmoke("modbus-tcp");
        var switched = switching.CommitForSmoke();
        if (switched.Host != "192.0.2.21" || switched.Properties["Extra"] != "draft" || tcp.Host != "192.0.2.20")
            throw new InvalidOperationException("Driver switch discarded typed/custom draft or mutated source.");
        switching.Close();
        using (var cancelled = new DeviceConfigurationDialog(tcp, _drivers))
        {
            cancelled.Show(this);
            cancelled.SetFieldForSmoke("Host", "192.0.2.99");
            ((Button)cancelled.CancelButton!).PerformClick();
            if (cancelled.Value is not null || tcp.Host != "192.0.2.20") throw new InvalidOperationException("Cancelled draft escaped.");
            cancelled.Close();
        }
        SmokeReadOnlyDeviceConfiguration();
        SmokeInvalidDeviceConfiguration();
        await Task.Yield();
        Console.WriteLine($"Driver parameters smoke passed: {_drivers.Drivers.Count} drivers, typed/custom round trips, add/edit/rename/delete/duplicate/reserved/masking/read-only, driver drafts and wide/narrow tabs; DeviceDpi={DeviceDpi}.");
    }
}
