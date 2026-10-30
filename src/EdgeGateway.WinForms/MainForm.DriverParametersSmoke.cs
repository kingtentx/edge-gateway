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
                if (editor.SmokeTabs.Count != 3 || editor.SmokeTabs.Any(tab => tab.Name == "DriverConfigurationParameters"))
                    throw new InvalidOperationException("Device parameters must use exactly three shared tabs.");
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
                    editor.SelectedSmokeTab = editor.SmokeTabs.Single(tab => tab.Name == "DeviceParametersTab");
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
        await VerifyParameterModalAsync(custom, null, "Endpoint", "fixture://new", false);
        await VerifyParameterModalAsync(custom, "Endpoint", "EndpointRenamed", "fixture://edited", false);
        await VerifyParameterModalAsync(custom, "EndpointRenamed", "EndpointRenamed", "cancelled", true);
        if (custom.Values["EndpointRenamed"] != "fixture://edited") throw new InvalidOperationException("Cancelled parameter edit changed draft.");
        await VerifyParameterModalAsync(custom, null, "EndpointRenamed", "duplicate", false, reject: true);
        await VerifyParameterModalAsync(custom, null, "", "missing-key", false, reject: true);
        await VerifyParameterModalAsync(custom, null, "Retries", "3", false);
        await VerifyParameterModalAsync(custom, null, "RetriesBad", "invalid-number", false, reject: true);
        await VerifyParameterModalAsync(custom, "Host", "Host", "192.0.2.21", false);
        VerifyEditorLayout(switching, "device parameter list");
        await Task.Delay(200);
        SaveEditorScreenshot(switching, "driver-parameters", "unified-list");
        switching.SetFieldForSmoke("Host", "192.0.2.21");
        switching.SelectDriverForSmoke("simulator");
        switching.SelectDriverForSmoke("modbus-tcp");
        var switched = switching.CommitForSmoke();
        if (switched.Host != "192.0.2.21" || switched.Properties["Extra"] != "draft" || tcp.Host != "192.0.2.20")
            throw new InvalidOperationException("Driver switch discarded typed/custom draft or mutated source.");
        if (switched.ParameterMetadata["Retries"].Name != "重试次数" || switched.ParameterMetadata["Retries"].TypeName != "Int32"
            || switched.ParameterMetadata["Retries"].Description != "失败重试上限") throw new InvalidOperationException("Parameter metadata lost across driver switch.");
        if (switched.ParameterMetadata["Host"].Name != "采集主机" || switched.ParameterMetadata["Host"].Description != "设备通信主机")
            throw new InvalidOperationException("Declared parameter display metadata was discarded.");
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
        Console.WriteLine($"Driver parameters smoke passed: {_drivers.Drivers.Count} drivers, typed/custom round trips, three shared tabs, actual add/edit/rename/cancel modals, delete/duplicate/empty-name/masking/read-only, driver drafts and wide/narrow tabs; DeviceDpi={DeviceDpi}.");
    }

    private async Task VerifyParameterModalAsync(DriverCustomParametersView view, string? originalKey, string key, string value, bool cancel, bool reject = false)
    {
        Exception? failure = null; var observed = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += async (_, _) =>
        {
            var dialog = System.Windows.Forms.Application.OpenForms.OfType<DeviceParameterDialog>().FirstOrDefault(form => form.Visible);
            if (dialog is null) return;
            timer.Stop(); observed = true;
            try
            {
                dialog.SetForSmoke(key, value);
                if (key.StartsWith("Retries", StringComparison.Ordinal)) dialog.SetMetadataForSmoke("重试次数", "Int32", "失败重试上限");
                else if (key == "Host")
                {
                    if (Descendants(dialog).Single(control => control.Name == "ParameterType").Enabled)
                        throw new InvalidOperationException("Declared wire type should remain read-only.");
                    dialog.SetMetadataForSmoke("采集主机", "String", "设备通信主机");
                }
                else if (key == "EndpointRenamed") dialog.SetMetadataForSmoke("服务地址", "String", "用于插件连接");
                VerifyEditorLayout(dialog, "parameter modal");
                var originalSize = dialog.Size;
                dialog.Width = Math.Max(dialog.MinimumSize.Width, UiTheme.Scale(dialog, 480));
                VerifyEditorLayout(dialog, "five-field parameter narrow modal");
                SaveEditorScreenshot(dialog, "driver-parameters", "edit-" + key + "-narrow");
                dialog.Size = originalSize;
                VerifyEditorLayout(dialog, "five-field parameter restored modal");
                SaveEditorScreenshot(dialog, "driver-parameters", "edit-" + key);
                if (cancel) ((Button)dialog.CancelButton!).PerformClick();
                else if (reject)
                {
                    dialog.SuppressApplyErrorDialogForSmoke = true;
                    try { await dialog.ApplyAndAcceptForSmokeAsync(); }
                    catch (EditorValidationException) { }
                    if (dialog.LastApplyErrorForSmoke is null || dialog.DialogResult == DialogResult.OK)
                        throw new InvalidOperationException("Invalid parameter name was accepted.");
                }
                else await dialog.ApplyAndAcceptForSmokeAsync();
            }
            catch (Exception exception) { failure = exception; }
            finally { dialog.Close(); }
        };
        timer.Start(); view.OpenForSmoke(originalKey); timer.Stop();
        if (failure is not null) throw failure;
        if (!observed) throw new InvalidOperationException("Parameter modal did not open.");
        await Task.Yield();
    }
}
