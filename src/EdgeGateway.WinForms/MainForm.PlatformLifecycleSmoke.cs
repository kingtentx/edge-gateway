using System.Net;
using System.Net.Sockets;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool PlatformLifecycleSmoke { get; set; }

    private async Task SmokePlatformLifecycleAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("PlatformLifecycle requires an isolated stopped host.");
        _refreshTimer.Stop();
        var original = Clone(_configuration);
        var originalSize = Size;
        // Reserve an unused local port without listening. Connection failure is
        // deliberate; this scenario must never contact a configured field broker.
        using var port = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        port.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("PlatformLifecycle: " + message); }
        async Task WaitFor(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition()) await Task.Delay(30, timeout.Token);
        }
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false;
            fixture.Mqtt = new() { Enabled = false, Host = "127.0.0.1", Port = ((IPEndPoint)port.LocalEndPoint!).Port, TimeoutMs = 1000 };
            fixture.Devices = [new() { Name = "热维护测试", DriverId = "simulator", PollIntervalMs = 100,
                Tags = [new() { Name = "本地点位", Address = "ramp", DataType = TagDataType.Float64, UploadEnabled = false }] }];
            await CommitConfigurationAsync(fixture, fixture.Devices[0].Id, replaceCloudDraft: true);
            await _application.StartDeviceAsync(fixture.Devices[0].Id);
            await WaitFor(() => _application.GetLatest().Count == 1);
            ShowPage("cloud");
            ((TextBox)_cloudFields["clientId"]).Text = "hot-ui-client";
            UpdateCommands();
            Check(_cloudApply.Enabled, "platform draft cannot apply during acquisition");
            using (RejectConfigurationWritesForSmoke())
            {
                var failed = false;
                try { await ApplyCloudSettingsAsync(); } catch { failed = true; }
                Check(failed && _dirty && ((TextBox)_cloudFields["clientId"]).Text == "hot-ui-client", "failed save lost draft");
                Check(_application.GetConfiguration().Mqtt.ClientId != "hot-ui-client", "failed save changed configuration");
            }
            await ApplyCloudSettingsAsync();
            Check(!_dirty && _application.GetConfiguration().Mqtt.ClientId == "hot-ui-client", "hot save did not apply");
            ((CheckBox)_cloudFields["mqttEnabled"]).Checked = true;
            await ApplyCloudSettingsAsync();
            Check(_application.GetTelemetryStatus().Running, "enabling platform did not start independent task");
            UpdateCommands();
            _platformPageView.StopPlatformButton.PerformClick();
            await WaitFor(() => !_busy && !_application.GetTelemetryStatus().Running);
            var timestamp = _application.GetLatest()[0].Timestamp;
            await WaitFor(() => _application.GetLatest()[0].Timestamp > timestamp);
            Check(_application.IsDeviceRunning(fixture.Devices[0].Id), "platform stop interrupted acquisition");
            ((TextBox)_cloudFields["clientId"]).Text = "unapplied-ui-draft";
            UpdateCommands();
            _platformPageView.StartPlatformButton.PerformClick();
            await WaitFor(() => !_busy && _application.GetTelemetryStatus().Running);
            Check(_dirty && _application.GetConfiguration().Mqtt.ClientId == "hot-ui-client", "start used unsaved draft");
            await ApplyCloudSettingsAsync();
            Check(_application.GetTelemetryStatus().Running && !_dirty, "hot reconnect lost platform state");
            foreach (var width in new[] { originalSize.Width, MinimumSize.Width }.Distinct())
            {
                Size = new Size(width, originalSize.Height);
                foreach (TabPage tab in _cloudTabs.TabPages)
                {
                    _cloudTabs.SelectedTab = tab;
                    await Task.Delay(80);
                    PerformLayoutRecursively(this);
                    AssertActionButtonSizes(_platformPageView, "platform lifecycle");
                    foreach (var button in new[] { _cloudApply, _cloudReset, _platformPageView.StartPlatformButton, _platformPageView.StopPlatformButton })
                        BringIntoViewAndAssert(button, "platform lifecycle " + button.Name);
                    SaveEditorScreenshot(this, "platform-lifecycle", tab.Name + "-" + width);
                }
            }
            ((TextBox)_cloudFields["name"]).Text = _configuration.Name + " draft";
            ((TextBox)_cloudFields["gatewayId"]).Text = "hot-ui-gateway";
            ((CheckBox)_cloudFields["autoStart"]).Checked = true;
            ((TextBox)_cloudFields["deviceType"]).Text = "mixed-platform-draft";
            UpdateCommands();
            Check(_cloudApply.Enabled, "gateway draft blocked platform apply");
            _cloudApply.PerformClick();
            await WaitFor(() => !_busy && !_dirty);
            var saved = await _application.ReadSavedConfigurationAsync();
            Check(saved.GatewayId == "hot-ui-gateway" && saved.AutoStart && saved.Name.EndsWith(" draft")
                && saved.Mqtt.PlatformDeviceType == "mixed-platform-draft", "mixed gateway/platform draft was not persisted");
            timestamp = _application.GetLatest()[0].Timestamp;
            await WaitFor(() => _application.GetLatest()[0].Timestamp > timestamp);
            Check(_application.IsDeviceRunning(fixture.Devices[0].Id), "device stopped during maintenance");
            SaveEditorScreenshot(this, "platform-lifecycle", "mixed-draft-applied");
            Console.WriteLine($"PlatformLifecycle smoke passed: DeviceDpi={DeviceDpi}; hot apply, failed save retains draft, independent start/stop, saved settings isolation, mixed gateway/platform hot apply, all platform tabs wide/narrow; local refused connection only.");
        }
        finally
        {
            await _application.StopAsync();
            ResetCloudDraft();
            await CommitConfigurationAsync(original, replaceCloudDraft: true);
            Size = originalSize;
        }
    }
}
