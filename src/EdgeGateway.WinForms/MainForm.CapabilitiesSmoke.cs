using EdgeGateway.Model;
using System.Diagnostics;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeCompletedCapabilitiesAsync()
    {
        if (!_smokeTest || _runtime.IsRunning || _dirty)
            throw new InvalidOperationException("Capabilities smoke requires stopped, saved, isolated fixtures.");
        var original = Clone(_configuration);
        var originalPage = _activePage;
        var originalSelection = SelectedDevice()?.Id;
        var originalChannel = _deviceChannelFilter.SelectedItem as ChannelChoice;
        var originalGroup = (_deviceGroupFilter.SelectedItem as GroupChoice)?.Group;
        var originalSearch = _deviceSearch.Text;
        try
        {
            var raw = new TagConfiguration { Name = "可写原始点", Address = "constant:1", DataType = TagDataType.Float64, Writable = true };
            var calculated = new TagConfiguration { Name = "表达式点", Address = "constant:3", DataType = TagDataType.Float64 };
            var a = new DeviceConfiguration
            {
                Name = "能力验收 A", Group = "产线 A", DriverId = "simulator", PollIntervalMs = 100,
                Tags = [raw, calculated, new TagConfiguration
                {
                    Name = "保留的停用点", Address = "constant:17", DataType = TagDataType.Float64,
                    Enabled = false, UploadEnabled = false, Scale = 2, Offset = 1,
                    Expression = "x + 9", ScanIntervalMs = 500, Deadband = .5, Description = "复制时保留全部配置"
                }]
            };
            var b = new DeviceConfiguration
            {
                Name = "能力验收 B", Group = "产线 B", DriverId = "simulator", PollIntervalMs = 100,
                Tags = [new TagConfiguration { Name = "SearchOnlyTag", Address = "constant:23", DataType = TagDataType.Float64 }]
            };
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt = new MqttConfiguration { Enabled = false };
            fixture.ReportMode = TelemetryReportMode.Periodic; fixture.PublishIntervalMs = 5000; fixture.HeartbeatIntervalMs = 60000;
            fixture.Channels = []; fixture.Devices = [a, b];
            await CommitConfigurationAsync(fixture, a.Id, replaceCloudDraft: true);
            ShowPage("devices");

            await SmokeCapabilityModalAsync<ChannelEditor>(() => EditChannelAsync(true), editor =>
            {
                CapabilityControl<TextBox>(editor, "ChannelName").Text = "能力验收通道";
                CapabilityControl<NumericUpDown>(editor, "ChannelDelay").Value = 25;
                CapabilityControl<TextBox>(editor, "ChannelDescription").Text = "共享通信调度；连接仍归各设备所有";
                VerifyEditorLayout(editor, "capabilities channel editor");
            }, "create channel");
            var channel = _configuration.Channels.Single();
            CheckCapability(channel.Id != Guid.Empty && channel.Name == "能力验收通道" && channel.InterRequestDelayMs == 25
                && channel.Description == "共享通信调度；连接仍归各设备所有", "channel fields were not saved.");
            await AssertSavedConfigurationForSmokeAsync("capabilities channel save");

            var edited = await SmokeDeviceConfigurationModalAsync(() => ConfigureDeviceAsync(a.Id), editor =>
            {
                editor.SetFieldForSmoke("Name", "能力验收 A 已保存");
                editor.SetFieldForSmoke("Group", "产线 A");
                editor.SetFieldForSmoke("ChannelId", channel.Id.ToString());
                editor.SetFieldForSmoke("ScanMode", nameof(DeviceScanMode.TagRate));
                editor.SetFieldForSmoke("AutoDemotionEnabled", "true");
                editor.SetFieldForSmoke("DemotionFailureCount", "4");
                editor.SetFieldForSmoke("DemotionIntervalMs", "9000");
                VerifyEditorLayout(editor, "capabilities device scheduling");
            }, "save device channel and scanning");
            CheckCapability(edited.Id == a.Id && edited.ChannelId == channel.Id && edited.ScanMode == DeviceScanMode.TagRate
                && edited.AutoDemotionEnabled && edited.DemotionFailureCount == 4 && edited.DemotionIntervalMs == 9000
                && edited.Tags.Select(tag => tag.Id).SequenceEqual(a.Tags.Select(tag => tag.Id)),
                "device scheduling save changed identity/tags or lost channel/scan/demotion fields.");
            using (var reopened = new DeviceConfigurationDialog(Clone(edited), _drivers, channels: _configuration.Channels))
            {
                reopened.Show(this);
                try { VerifyConfigurationSmokeFields(reopened, _drivers.GetFactory("simulator").Descriptor, edited); }
                finally { reopened.Close(); }
            }
            await AssertSavedConfigurationForSmokeAsync("capabilities device scheduling save");

            SmokeCapabilityFilters(a.Id, b.Id, channel.Id);
            RebindConfigurationViews(a.Id);
            await SmokeCapabilityModalAsync<TagEditor>(() => EditTagAsync(calculated.Id), editor =>
            {
                VerifyEditorLayout(editor, "capabilities expression editor");
                var expression = CapabilityControl<TextBox>(editor, "TagExpression");
                var input = CapabilityControl<TextBox>(editor, "ExpressionPreviewInput");
                var result = CapabilityControl<Label>(editor, "ExpressionPreviewResult");
                var preview = Descendants(editor).OfType<Button>().Single(button => button.Text == "预览表达式");
                SelectCapabilityTab(editor, expression);
                expression.Text = "x / (x - x)"; input.Text = "7";
                BringIntoViewAndAssert(preview, "capabilities invalid expression preview"); preview.PerformClick();
                CheckCapability(result.Text.StartsWith("无法计算：", StringComparison.Ordinal), "invalid preview did not report failure.");
                expression.Text = "clamp(x * 2 + 1, 0, 100)";
                BringIntoViewAndAssert(preview, "capabilities expression preview"); preview.PerformClick();
                CheckCapability(result.Text == "计算结果：15", "preview did not evaluate the operator's expression and x.");
                BringIntoViewAndAssert(result, "capabilities expression result");
                var scan = CapabilityControl<NumericUpDown>(editor, "TagScanInterval");
                SelectCapabilityTab(editor, scan);
                scan.Value = 250;
                CapabilityControl<TextBox>(editor, "TagDeadband").Text = "0.75";
                CheckCapability(_configuration.Devices.Single(device => device.Id == a.Id).Tags.Single(tag => tag.Id == calculated.Id).Expression.Length == 0,
                    "expression preview modified applied configuration before confirmation.");
            }, "preview and save expression/scan/deadband");
            var savedTag = _configuration.Devices.Single(device => device.Id == a.Id).Tags.Single(tag => tag.Id == calculated.Id);
            CheckCapability(savedTag.Expression == "clamp(x * 2 + 1, 0, 100)" && savedTag.ScanIntervalMs == 250 && savedTag.Deadband == .75,
                "point expression, scan interval or deadband was not committed.");
            await AssertSavedConfigurationForSmokeAsync("capabilities point policy save");

            var source = Clone(_configuration.Devices.Single(device => device.Id == a.Id));
            var sourceJson = ConfigurationSmokeJson(source);
            var previousIds = _configuration.Devices.Select(device => device.Id).ToHashSet();
            var previousTagIds = _configuration.Devices.SelectMany(device => device.Tags).Select(tag => tag.Id).ToHashSet();
            RebindConfigurationViews(a.Id);
            var copy = await SmokeDeviceConfigurationModalAsync(CopyDeviceAsync, editor =>
            {
                CheckCapability(!CapabilityControl<CheckBox>(editor, "DeviceConfiguration_Enabled").Checked,
                    "copy draft is enabled before an operator reviews its connection.");
                VerifyEditorLayout(editor, "capabilities copy device");
            }, "copy complete device");
            CheckCapability(copy.Id != Guid.Empty && !previousIds.Contains(copy.Id) && !copy.Enabled
                && copy.Tags.Count == source.Tags.Count && copy.Tags.Select(tag => tag.Id).Distinct().Count() == copy.Tags.Count
                && copy.Tags.All(tag => tag.Id != Guid.Empty && !previousTagIds.Contains(tag.Id)) && !_runtime.IsRunning,
                "copy reused identities, lost points, became enabled or started collection.");
            var expectedCopy = Clone(source);
            expectedCopy.Id = copy.Id; expectedCopy.Name = copy.Name; expectedCopy.Enabled = false;
            for (var i = 0; i < expectedCopy.Tags.Count; i++) expectedCopy.Tags[i].Id = copy.Tags[i].Id;
            AssertConfigurationSmokeEqual(expectedCopy, copy, "capabilities full copy content");
            AssertConfigurationSmokeUnchanged(sourceJson, _configuration.Devices.Single(device => device.Id == a.Id), "capabilities copy source isolation");
            CheckCapability(_configuration.Devices.Count == previousIds.Count + 1
                && _configuration.Devices.Count(device => string.Equals(device.Name, copy.Name, StringComparison.OrdinalIgnoreCase)) == 1,
                "copy did not create exactly one uniquely named device.");
            await AssertSavedConfigurationForSmokeAsync("capabilities copied device save");

            await SmokeCapabilityCloudSettingsAsync();
            var probe = await _runtime.ProbeDeviceAsync(a.Id, raw.Id,
                new DeviceProbeOptions { TimeoutMs = 3000, Source = "windows-capabilities-smoke" });
            CheckCapability(probe.State == DeviceProbeState.Succeeded && probe.Connected && !probe.UsedRunningSession
                && probe.Sample is { Quality: SampleQuality.Good, NumericValue: 1 } && probe.Sample.TagId == raw.Id
                && probe.Sample.DeviceId == a.Id && probe.Sample.Timestamp >= probe.StartedAt
                && probe.Sample.Timestamp <= probe.CompletedAt && !_runtime.IsRunning,
                "stopped-device probe did not return a fresh simulator read and release its temporary session: " + probe.Error);

            RebindConfigurationViews(a.Id); ShowPage("devices");
            await DeviceRowActionAsync(a.Id, DeviceStartColumnName);
            var deadline = Stopwatch.StartNew();
            while (!_runtime.GetLatest().Any(sample => sample.DeviceId == a.Id && sample.TagId == calculated.Id
                       && sample.Quality == SampleQuality.Good && sample.NumericValue == 7) && deadline.Elapsed < TimeSpan.FromSeconds(8))
                await Task.Delay(25);
            CheckCapability(_runtime.IsDeviceRunning(a.Id) && _runtime.GetLatest().Any(sample => sample.TagId == calculated.Id
                && sample.Quality == SampleQuality.Good && sample.NumericValue == 7), "saved expression did not execute during real simulator acquisition.");
            var verification = SmokeCapabilityWriteOptions();
            var written = await _runtime.WriteVerifiedAsync(a.Id, raw.Id, "42.5", verification with { Source = "windows-capabilities-smoke" });
            CheckCapability(written.State == WriteCommandState.Matched && written.WriteAttempted && written.Submitted
                && written.VerificationRequested && written.ReadBackAttempts >= 1
                && written.ReadBack is { Quality: SampleQuality.Good, NumericValue: 42.5 }
                && written.ReadBack.TagId == raw.Id && written.ReadBack.DeviceId == a.Id
                && written.ReadBack.Timestamp >= written.StartedAt && written.ReadBack.Timestamp <= written.CompletedAt
                && !_configuration.Mqtt.Enabled && !_runtime.IsDeviceRunning(copy.Id),
                "write/readback did not confirm a fresh simulator response or affected another session: " + written.Error);
            await AssertSavedConfigurationForSmokeAsync("capabilities operations preserve saved configuration");
            Console.WriteLine($"Completed capabilities smoke passed at actual {DeviceDpi} DPI: channel/device policy save and reopen; group/channel/name/address filters; disabled full copy with new IDs; expression failure/success preview, saved scan/deadband and acquisition; all platform/RPC fields saved; fresh stopped probe and matched write/readback. Only simulators ran; MQTT remained disabled.");
        }
        catch { TrySaveSmokeFailure(this, "completed-capabilities-failed"); throw; }
        finally
        {
            await _runtime.StopAsync();
            await RestoreConfigurationForSmokeAsync(original);
            RebindConfigurationViews(originalSelection);
            _bindingDeviceFilters = true;
            try
            {
                _deviceChannelFilter.SelectedItem = _deviceChannelFilter.Items.Cast<ChannelChoice>()
                    .FirstOrDefault(choice => choice.Id == originalChannel?.Id && choice.IndependentOnly == (originalChannel?.IndependentOnly ?? false))
                    ?? _deviceChannelFilter.Items[0];
                _deviceGroupFilter.SelectedItem = _deviceGroupFilter.Items.Cast<GroupChoice>()
                    .FirstOrDefault(choice => string.Equals(choice.Group, originalGroup, StringComparison.OrdinalIgnoreCase)) ?? _deviceGroupFilter.Items[0];
                _deviceSearch.Text = originalSearch;
            }
            finally { _bindingDeviceFilters = false; }
            BindDeviceRows(originalSelection); ShowPage(originalPage); UpdateCommands();
        }
    }

    private void SmokeCapabilityFilters(Guid a, Guid b, Guid channel)
    {
        void Rows(params Guid[] expected)
        {
            var actual = _deviceGrid.Rows.Cast<DataGridViewRow>().Select(row => ((DeviceRow)row.DataBoundItem!).Id).ToArray();
            CheckCapability(actual.Length == expected.Length && actual.ToHashSet().SetEquals(expected), "device filter returned unexpected identities.");
            if (expected.Length == 0) CheckCapability(SelectedDevice() is null && _tagGrid.Rows.Count == 0, "empty filter retained a stale selected device or points.");
        }
        _deviceChannelFilter.SelectedIndex = 0; _deviceGroupFilter.SelectedIndex = 0; _deviceSearch.Clear(); Rows(a, b);
        _deviceGroupFilter.SelectedItem = _deviceGroupFilter.Items.Cast<GroupChoice>().Single(choice => choice.Group == "产线 A"); Rows(a);
        _deviceSearch.Text = "sEaRcHoNlY"; Rows();
        _deviceGroupFilter.SelectedIndex = 0; Rows(b);
        _deviceSearch.Text = "constant:23"; Rows(b);
        _deviceSearch.Text = "SIMULATOR"; Rows(a, b);
        _deviceSearch.Text = "能力验收 A 已保存"; Rows(a);
        _deviceSearch.Clear();
        _deviceChannelFilter.SelectedItem = _deviceChannelFilter.Items.Cast<ChannelChoice>().Single(choice => choice.Id == channel); Rows(a);
        _deviceChannelFilter.SelectedItem = _deviceChannelFilter.Items.Cast<ChannelChoice>().Single(choice => choice.IndependentOnly); Rows(b);
        var clear = Descendants(_pages["devices"]).OfType<Button>().Single(button => button.Text == "清除筛选");
        BringIntoViewAndAssert(clear, "capabilities clear filters"); clear.PerformClick(); Rows(a, b);
    }

    private async Task SmokeCapabilityCloudSettingsAsync()
    {
        var beforeDevices = _configuration.Devices.Select(ConfigurationSmokeJson).ToArray();
        ShowPage("cloud");
        void Text(string key, string value) => ((TextBox)_cloudFields[key]).Text = value;
        void Number(string key, decimal value) => ((NumericUpDown)_cloudFields[key]).Value = value;
        Text("usernameEnv", "EDGE_SMOKE_TOKEN"); Text("passwordEnv", "EDGE_SMOKE_PASSWORD");
        Text("deviceType", "smoke-type"); Text("typeKey", "smoke-type-key"); Text("platformGatewayId", "smoke-gateway");
        Text("rpcRequest", "smoke/{gatewayId}/rpc/request"); Text("rpcResponse", "smoke/{gatewayId}/rpc/response");
        ((CheckBox)_cloudFields["rpcEnabled"]).Checked = true;
        ((CheckBox)_cloudFields["remoteWrite"]).Checked = true;
        Number("rpcTimeout", 7500); Number("publishInterval", 500); Number("heartbeatInterval", 7000);
        ((ComboBox)_cloudFields["reportMode"]).SelectedItem = TelemetryReportMode.OnChangeWithHeartbeat;
        foreach (var platform in Enum.GetValues<MqttPlatform>())
        {
            ((ComboBox)_cloudFields["platform"]).SelectedItem = platform;
            await ApplyCloudSettingsAsync();
            var mqtt = _configuration.Mqtt;
            CheckCapability(!_dirty && !mqtt.Enabled && mqtt.Platform == platform && mqtt.RpcEnabled && mqtt.AllowRemoteWrite
                && mqtt.RpcTimeoutMs == 7500 && mqtt.UsernameEnvironmentVariable == "EDGE_SMOKE_TOKEN"
                && mqtt.PasswordEnvironmentVariable == "EDGE_SMOKE_PASSWORD" && mqtt.PlatformDeviceType == "smoke-type"
                && mqtt.ThingsCloudTypeKey == "smoke-type-key" && mqtt.PlatformGatewayId == "smoke-gateway"
                && mqtt.RpcRequestTopic == "smoke/{gatewayId}/rpc/request" && mqtt.RpcResponseTopic == "smoke/{gatewayId}/rpc/response"
                && _configuration.ReportMode == TelemetryReportMode.OnChangeWithHeartbeat
                && _configuration.PublishIntervalMs == 500 && _configuration.HeartbeatIntervalMs == 7000,
                "platform/RPC/report fields did not save for " + platform + ".");
            CheckCapability(((ComboBox)_cloudFields["platform"]).SelectedItem is MqttPlatform shown && shown == platform
                && ((CheckBox)_cloudFields["rpcEnabled"]).Checked && ((CheckBox)_cloudFields["remoteWrite"]).Checked
                && ((NumericUpDown)_cloudFields["rpcTimeout"]).Value == 7500
                && ((NumericUpDown)_cloudFields["heartbeatInterval"]).Value == 7000,
                "saved platform/RPC/report fields did not rebind for " + platform + ".");
            await AssertSavedConfigurationForSmokeAsync("capabilities " + platform + " settings");
        }
        CheckCapability(beforeDevices.SequenceEqual(_configuration.Devices.Select(ConfigurationSmokeJson)), "platform apply replaced saved device/channel policy content.");
    }

    private WriteVerificationOptions SmokeCapabilityWriteOptions()
    {
        using var editor = new WriteValueDialog("能力验收 A", "可写原始点", "", "1", TagDataType.Float64);
        editor.Show(this);
        try
        {
            editor.SmokeValueInput.Text = "42.5";
            CapabilityControl<NumericUpDown>(editor, "WriteVerificationTimeout").Value = 4000;
            CapabilityControl<TextBox>(editor, "WriteAbsoluteTolerance").Text = "0.01";
            CapabilityControl<TextBox>(editor, "WriteRelativeTolerance").Text = "0.001";
            foreach (var input in editor.SmokeVerificationInputs) BringIntoViewAndAssert(input, "capabilities write verification field");
            var accept = (Button)editor.AcceptButton!;
            BringIntoViewAndAssert(accept, "capabilities write verification accept"); accept.PerformClick();
            var options = editor.VerificationOptions;
            CheckCapability(editor.DialogResult == DialogResult.OK && editor.Value == "42.5" && options.VerifyReadBack
                && options.TimeoutMs == 4000 && options.AbsoluteTolerance == .01 && options.RelativeTolerance == .001,
                "write verification dialog did not preserve the operator's options.");
            return options;
        }
        finally { editor.Close(); }
    }

    private async Task SmokeCapabilityModalAsync<T>(Func<Task> open, Action<T> edit, string context) where T : EditorDialog
    {
        var observed = false;
        Exception? failure = null;
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += async (_, _) =>
        {
            var editor = Application.OpenForms.OfType<T>().SingleOrDefault(window => window.Visible && window.Modal);
            if (editor is null)
            {
                if (elapsed.Elapsed < TimeSpan.FromSeconds(8)) return;
                timer.Stop(); failure = new InvalidOperationException(context + ": expected editor did not open.");
                foreach (var modal in Application.OpenForms.Cast<Form>().Where(window => window.Modal).ToArray()) modal.Close();
                return;
            }
            timer.Stop(); observed = true;
            try { edit(editor); await editor.ApplyAndAcceptForSmokeAsync(); }
            catch (Exception exception)
            {
                failure = exception; TrySaveSmokeFailure(editor, "capability-editor-failed"); editor.DialogResult = DialogResult.Cancel;
            }
            finally { editor.Close(); }
        };
        timer.Start();
        try { await open(); }
        finally { timer.Stop(); }
        if (!observed || failure is not null) throw new InvalidOperationException("Capabilities smoke: " + context + " failed.", failure);
    }

    private static T CapabilityControl<T>(Control root, string name) where T : Control =>
        Descendants(root).OfType<T>().Single(control => control.Name == name);

    private static void SelectCapabilityTab(EditorDialog editor, Control input)
    {
        editor.SelectedSmokeTab = editor.SmokeTabs.Single(tab => Descendants(tab).Contains(input));
        PerformLayoutRecursively(editor);
    }

    private static void CheckCapability(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Capabilities smoke: " + message);
    }
}
