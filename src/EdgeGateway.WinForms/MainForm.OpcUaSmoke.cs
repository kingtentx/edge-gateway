using EdgeGateway.Model;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeOpcUaServiceAsync()
    {
        if (!_smokeTest || _runtime.IsRunning || _dirty || _opcUaDirty || _configuration.OpcUa.Enabled)
            throw new InvalidOperationException("OPC UA UI smoke requires stopped, saved, isolated fixtures with the external service initially disabled.");
        var original = Clone(_configuration);
        var originalPage = _activePage;
        var originalSelection = SelectedDevice()?.Id;
        var passwordVariable = "EDGE_OPCUA_UI_SMOKE_" + Guid.NewGuid().ToString("N");
        var previousPassword = Environment.GetEnvironmentVariable(passwordVariable);
        async Task WaitFor(Func<bool> predicate, string message)
        {
            var elapsed = Stopwatch.StartNew();
            while (!predicate() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            AssertOpcUaSmoke(predicate(), message);
        }
        try
        {
            var tag = new TagConfiguration { Name = "UA界面验证点", Address = "constant:7", DataType = TagDataType.Float64, Writable = true };
            var device = new DeviceConfiguration
            {
                Name = "UA界面验证设备", DriverId = "simulator", PollIntervalMs = 100, Tags = [tag]
            };
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt = new MqttConfiguration { Enabled = false };
            fixture.OpcUa = new OpcUaServerConfiguration(); fixture.Channels = []; fixture.Devices = [device];
            await CommitConfigurationAsync(fixture, device.Id, replaceCloudDraft: true);
            ResetOpcUaDraft(); BindOpcUaFields(); ShowPage("opcua"); UpdateCommands();
            AssertOpcUaSmoke(_opcUaFields.Count == 10 && !_opcUaStart.Enabled && !_opcUaStop.Enabled,
                "the disabled service exposes missing fields or enabled start/stop commands.");
            using var portLease = new TcpListener(IPAddress.Loopback, 0);
            portLease.Start(); var port = ((IPEndPoint)portLease.LocalEndpoint).Port; portLease.Stop();
            var endpoint = $"opc.tcp://127.0.0.1:{port}/EdgeGateway";
            ((CheckBox)_opcUaFields["enabled"]).Checked = true;
            ((TextBox)_opcUaFields["endpoint"]).Text = endpoint;
            ((CheckBox)_opcUaFields["anonymous"]).Checked = false;
            ((CheckBox)_opcUaFields["userAuthentication"]).Checked = true;
            ((TextBox)_opcUaFields["username"]).Text = "ui-smoke-operator";
            ((TextBox)_opcUaFields["passwordEnv"]).Text = passwordVariable;
            ((CheckBox)_opcUaFields["remoteWrite"]).Checked = true;
            ((NumericUpDown)_opcUaFields["updateInterval"]).Value = 125;
            ((NumericUpDown)_opcUaFields["maxSessions"]).Value = 3;
            ((NumericUpDown)_opcUaFields["operationTimeout"]).Value = 3456;
            foreach (var input in _opcUaFields.Values) BringIntoViewAndAssert(input, "OPC UA settings smoke field");
            AssertOpcUaSmoke(_opcUaDirty && !_configuration.OpcUa.Enabled && _opcUaDraft?.EndpointUrl == endpoint,
                "editing UA fields modified the applied configuration or failed to create a draft.");

            // Keep a separate platform draft throughout the UA commit and device edits.
            ((TextBox)_cloudFields["name"]).Text = original.Name + " · 平台待应用";
            Environment.SetEnvironmentVariable(passwordVariable, null);
            await ApplyOpcUaSettingsAsync();
            var saved = _configuration.OpcUa;
            AssertOpcUaSmoke(!_opcUaDirty && _dirty && _configuration.Name == original.Name && saved.Enabled
                && saved.EndpointUrl == endpoint && !saved.AllowAnonymous && saved.EnableUserAuthentication
                && saved.Username == "ui-smoke-operator" && saved.PasswordEnvironmentVariable == passwordVariable
                && saved.AllowRemoteWrite && saved.UpdateIntervalMs == 125 && saved.MaxSessions == 3 && saved.OperationTimeoutMs == 3456,
                "UA apply lost a field, applied the separate platform draft, or failed to save.");
            await AssertSavedConfigurationForSmokeAsync("OPC UA saved settings with visible startup failure");
            RefreshOpcUaStatus();
            AssertOpcUaSmoke(!_opcUaServer.GetStatus().IsRunning && _opcUaServer.GetStatus().Enabled
                && _opcUaStatusFields["error"].Text.Contains(passwordVariable, StringComparison.Ordinal)
                && _opcUaStart.Enabled && !_opcUaStop.Enabled,
                "a missing password environment variable was hidden or an enabled service was reported as running.");

            Environment.SetEnvironmentVariable(passwordVariable, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            await StartOpcUaServiceAsync();
            await WaitFor(() => _opcUaServer.GetStatus() is { IsRunning: true, NodeCount: > 0 }, "a retry did not start the real secure UA endpoint.");
            RefreshOpcUaStatus();
            AssertOpcUaSmoke(_opcUaStatusFields["error"].Text == "无" && !_opcUaStart.Enabled && _opcUaStop.Enabled,
                "the successful retry did not clear the UI error and update service commands.");
            using (var publicCertificate = new X509Certificate2(await _opcUaServer.ExportServerCertificateAsync()))
                AssertOpcUaSmoke(!publicCertificate.HasPrivateKey && publicCertificate.Thumbprint == _opcUaServer.GetStatus().CertificateThumbprint,
                    "server certificate export contains a private key or differs from the displayed certificate.");

            await _runtime.StartDeviceAsync(device.Id);
            await WaitFor(() => _runtime.GetLatest().Any(sample => sample.TagId == tag.Id && sample.Quality == SampleQuality.Good),
                "the isolated simulator did not begin acquisition.");
            ((TextBox)_opcUaFields["username"]).Text = "pending-ui-username";
            UpdateCommands();
            AssertOpcUaSmoke(_opcUaDirty && _opcUaFields.Values.All(field => field.Enabled) && !_opcUaApply.Enabled && _opcUaStop.Enabled,
                "running acquisition locked the UA draft or made the apply/stop commands inconsistent.");
            var applyRejected = false;
            try { await ApplyOpcUaSettingsAsync(); }
            catch (InvalidOperationException) { applyRejected = true; }
            AssertOpcUaSmoke(applyRejected && _configuration.OpcUa.Username == "ui-smoke-operator" && _opcUaDirty,
                "global UA settings were applied while a device was collecting.");

            var beforeStop = _runtime.GetLatest().Single(sample => sample.TagId == tag.Id).Timestamp;
            await StopOpcUaServiceAsync();
            await WaitFor(() => _runtime.IsDeviceRunning(device.Id) && _runtime.GetLatest().Any(sample =>
                sample.TagId == tag.Id && sample.Timestamp > beforeStop && sample.Quality == SampleQuality.Good),
                "stopping the UA service interrupted acquisition.");
            var editedDevice = Clone(_configuration); editedDevice.Devices.Single().Name = "UA界面验证设备 已维护";
            await CommitConfigurationAsync(editedDevice, device.Id);
            AssertOpcUaSmoke(!_opcUaServer.GetStatus().IsRunning && _opcUaDirty && _dirty
                && ((TextBox)_opcUaFields["username"]).Text == "pending-ui-username",
                "a device commit restarted a manually stopped UA endpoint or discarded either independent draft.");
            await StartOpcUaServiceAsync();
            ResetOpcUaDraft(); BindOpcUaFields(); UpdateCommands();
            AssertOpcUaSmoke(!_opcUaDirty && _dirty && ((TextBox)_opcUaFields["username"]).Text == "ui-smoke-operator",
                "resetting the UA draft changed the platform draft or failed to restore saved values.");

            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=EdgeGateway UI Smoke Client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var client = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
            await _opcUaServer.TrustCertificateAsync(client.Export(X509ContentType.Cert));
            AssertOpcUaSmoke((await _opcUaServer.GetCertificatesAsync()).Any(certificate =>
                certificate.Store == OpcUaCertificateStore.Trusted && certificate.Thumbprint == client.Thumbprint),
                "trusting the isolated public client certificate did not update the real trust store.");
            using (var certificates = new OpcUaCertificatesDialog(_opcUaServer, _log))
            {
                await certificates.RefreshCertificatesAsync(); certificates.Show(this);
                try
                {
                    VerifyOpcUaCertificateDialog(certificates, "OPC UA certificate UI smoke");
                    AssertOpcUaSmoke(certificates.SmokeCertificateGrid.Rows.Cast<DataGridViewRow>().Any(row =>
                        row.Cells["Thumbprint"].Value?.ToString() == client.Thumbprint),
                        "the certificate grid did not display the stored client thumbprint.");
                }
                finally { certificates.Close(); }
            }
            var beforeRegeneration = _runtime.GetLatest().Single(sample => sample.TagId == tag.Id).Timestamp;
            var oldThumbprint = _opcUaServer.GetStatus().CertificateThumbprint;
            await _opcUaServer.RegenerateServerCertificateAsync();
            AssertOpcUaSmoke(_opcUaServer.GetStatus().IsRunning && _opcUaServer.GetStatus().CertificateThumbprint != oldThumbprint
                && (await _opcUaServer.GetCertificatesAsync()).Any(certificate => certificate.Store == OpcUaCertificateStore.Trusted
                    && certificate.Thumbprint == client.Thumbprint),
                "server certificate regeneration failed to replace its certificate, resume UA, or preserve client trust.");
            using (var replacement = new X509Certificate2(await _opcUaServer.ExportServerCertificateAsync()))
                AssertOpcUaSmoke(!replacement.HasPrivateKey && replacement.Thumbprint == _opcUaServer.GetStatus().CertificateThumbprint,
                    "the regenerated server public certificate differs from status or contains a private key.");
            await _opcUaServer.RemoveTrustedCertificateAsync(client.Thumbprint);
            AssertOpcUaSmoke(!(await _opcUaServer.GetCertificatesAsync()).Any(certificate =>
                certificate.Store == OpcUaCertificateStore.Trusted && certificate.Thumbprint == client.Thumbprint)
                && _opcUaServer.GetStatus().IsRunning, "revoking client trust failed or did not restore the running endpoint.");
            await WaitFor(() => _runtime.IsDeviceRunning(device.Id) && _runtime.GetLatest().Any(sample => sample.TagId == tag.Id
                && sample.Timestamp > beforeRegeneration && sample.Quality == SampleQuality.Good),
                "certificate replacement/revocation interrupted simulator acquisition.");
            await _runtime.StopAsync();
            AssertOpcUaSmoke(_opcUaServer.GetStatus().IsRunning, "stopping device collection stopped the independent UA endpoint.");
            await StopOpcUaServiceAsync();
            await AssertSavedConfigurationForSmokeAsync("OPC UA runtime/certificate operations preserve settings");
            Console.WriteLine($"OPC UA service UI smoke passed at actual {DeviceDpi} DPI: all settings persisted; independent drafts; visible startup failure and retry; secure loopback endpoint start/stop while simulator collection continues; public certificate export, explicit trust-store API roundtrip, server certificate replacement preserving client trust and revocation; certificate editor focus/scroll. UI confirmation dialogs are retained; no external device or platform was contacted.");
        }
        catch { TrySaveSmokeFailure(this, "opcua-service-ui-failed"); throw; }
        finally
        {
            try { await _opcUaServer.StopAsync(); }
            finally
            {
                await _runtime.StopAsync();
                Environment.SetEnvironmentVariable(passwordVariable, previousPassword);
                await RestoreConfigurationForSmokeAsync(original);
                ResetOpcUaDraft(); _opcUaUiError = null;
                RebindConfigurationViews(originalSelection); ShowPage(originalPage); UpdateCommands();
            }
        }
    }

    private static void VerifyOpcUaCertificateDialog(OpcUaCertificatesDialog editor, string context)
    {
        VerifyEditorLayout(editor, context);
        foreach (var command in editor.SmokeCertificateCommands)
        {
            SelectCapabilityTab(editor, command);
            BringIntoViewAndAssert(command, context + " certificate command");
        }
        foreach (var input in editor.SmokeManagementInputs)
        {
            SelectCapabilityTab(editor, input);
            BringIntoViewAndAssert(input, context + " certificate field");
        }
        SelectCapabilityTab(editor, editor.SmokeCertificateGrid);
        BringIntoViewAndAssert(editor.SmokeCertificateGrid, context + " certificate list");
        ResetScrollPositions(editor.SelectedSmokeTab!, editor.SmokeCommands.Last());
    }

    private static void AssertOpcUaSmoke(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("OPC UA UI smoke: " + message);
    }
}
