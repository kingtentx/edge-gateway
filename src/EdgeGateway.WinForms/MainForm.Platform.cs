using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly Dictionary<string, Control> _cloudFields = [];
    private GatewayConfiguration? _cloudDraft;
    private bool _binding;
    private TabControl _cloudTabs = null!;
    private Label _cloudApplyState = null!;
    private AntdUI.Button _cloudApply = null!, _cloudReset = null!;
    private PlatformPageView _platformPageView = null!;

    private Control BuildCloudPage()
    {
        _platformPageView = new PlatformPageView();
        _cloudTabs = _platformPageView.PlatformTabs;
        _cloudApplyState = _platformPageView.DraftState;
        _cloudApply = _platformPageView.ApplyButton;
        _cloudReset = _platformPageView.ResetButton;
        foreach (var field in _platformPageView.Fields)
        {
            _cloudFields.Add(field.Key, field.Value);
            _configurationCommands.Add(field.Value);
        }

        // All fixed editors and their parent/child relationships live in the view's
        // InitializeComponent. These helpers only bind existing controls to a draft.
        void BindText(string key, Action<string> update)
        {
            var input = (TextBox)_cloudFields[key];
            input.TextChanged += (_, _) => { if (!_binding) { update(input.Text); MarkDirty(); } };
        }
        void BindNumber(string key, Action<int> update)
        {
            var input = (NumericUpDown)_cloudFields[key];
            input.ValueChanged += (_, _) => { if (!_binding) { update((int)input.Value); MarkDirty(); } };
        }
        void BindCheck(string key, Action<bool> update)
        {
            var input = (CheckBox)_cloudFields[key];
            input.CheckedChanged += (_, _) => { if (!_binding) { update(input.Checked); MarkDirty(); } };
        }
        void BindChoice<T>(string key, Action<T> update, Func<T, string>? display = null) where T : struct, Enum
        {
            var input = (ComboBox)_cloudFields[key];
            EditorLayout.ConfigureDropDown(input, 520, 320);
            input.Items.AddRange(Enum.GetValues<T>().Cast<object>().ToArray());
            if (display is not null) input.Format += (_, e) => { if (e.ListItem is T value) e.Value = display(value); };
            input.SelectedIndexChanged += (_, _) => { if (!_binding && input.SelectedItem is T value) { update(value); MarkDirty(); } };
        }

        BindCheck("mqttEnabled", x => CloudDraft.Mqtt.Enabled = x);
        BindChoice<MqttPlatform>("platform", x => CloudDraft.Mqtt.Platform = x);
        BindText("host", x => CloudDraft.Mqtt.Host = x);
        BindNumber("port", x => CloudDraft.Mqtt.Port = x);
        BindCheck("tls", x => CloudDraft.Mqtt.UseTls = x);
        BindText("clientId", x => CloudDraft.Mqtt.ClientId = x);
        BindNumber("mqttTimeout", x => CloudDraft.Mqtt.TimeoutMs = x);
        BindText("username", x => CloudDraft.Mqtt.Username = x);
        BindText("accessToken", x => CloudDraft.Mqtt.AccessToken = x);
        BindText("password", x => CloudDraft.Mqtt.Password = x);
        BindText("topic", x => CloudDraft.Mqtt.Topic = x);
        BindText("deviceType", x => CloudDraft.Mqtt.PlatformDeviceType = x);
        BindText("typeKey", x => CloudDraft.Mqtt.ThingsCloudTypeKey = x);
        BindText("platformGatewayId", x => CloudDraft.Mqtt.PlatformGatewayId = x);
        BindChoice<TelemetryReportMode>("reportMode", x => CloudDraft.ReportMode = x,
            x => x switch { TelemetryReportMode.OnChange => "仅变化上报", TelemetryReportMode.OnChangeWithHeartbeat => "变化上报 + 周期心跳", _ => "周期全量上报" });
        BindNumber("publishInterval", x => CloudDraft.PublishIntervalMs = x);
        BindNumber("heartbeatInterval", x => CloudDraft.HeartbeatIntervalMs = x);
        BindNumber("maxOutbox", x => CloudDraft.MaxOutboxMessages = x);
        BindCheck("rpcEnabled", x => CloudDraft.Mqtt.RpcEnabled = x);
        BindCheck("remoteWrite", x => CloudDraft.Mqtt.AllowRemoteWrite = x);
        BindNumber("rpcTimeout", x => CloudDraft.Mqtt.RpcTimeoutMs = x);
        BindText("rpcRequest", x => CloudDraft.Mqtt.RpcRequestTopic = x);
        BindText("rpcResponse", x => CloudDraft.Mqtt.RpcResponseTopic = x);
        BindText("gatewayId", x => CloudDraft.GatewayId = x);
        BindText("name", x => CloudDraft.Name = x);
        BindCheck("autoStart", x => CloudDraft.AutoStart = x);

        _platformPageView.ManageDriversButton.Click += (_, _) =>
        {
            using var dialog = new DriverPluginDialog(_application, _driverDirectory);
            dialog.ShowDialog(this);
            RebindConfigurationViews();
        };
        _cloudApply.Click += async (_, _) => await RunBusyAsync(ApplyCloudSettingsAsync);
        _cloudReset.Click += (_, _) =>
        {
            if (!CanEdit()) return;
            ResetCloudDraft(); BindCloudFields(); UpdateCommands();
            SetFooter("平台设置已恢复为上次保存的内容。");
        };
        _configurationCommands.AddRange([_cloudApply, _cloudReset]);
        _stoppedConfigurationCommands.Add(_cloudApply);
        return _platformPageView;
    }

    private void UpdatePlatformCommands()
    {
        if (_cloudApply is null || _cloudReset is null) return;
        _cloudApply.Enabled = CanEdit() && _dirty && !_application.IsRunning;
        _cloudReset.Enabled = CanEdit() && _dirty;
        _cloudApplyState.Text = _application.IsRunning
            ? (_dirty ? "有未应用的修改。请先停止全部设备，再应用设置；当前采集继续使用已保存配置。" : "当前有设备运行，可编辑草稿；应用全局设置前需停止全部设备。")
            : _dirty ? "有未应用的修改。点击“应用设置”后生效。" : "设置已应用。修改后可在此应用或重置。";
        _cloudApplyState.ForeColor = _dirty ? UiTheme.Warning : UiTheme.Muted;
        var platform = CloudDraft.Mqtt.Platform;
        foreach (var key in new[] { "topic", "rpcRequest", "rpcResponse" })
            _cloudFields[key].Enabled = CanEdit() && platform == MqttPlatform.Generic;
        _cloudFields["typeKey"].Enabled = CanEdit() && platform == MqttPlatform.ThingsCloud;
        _cloudFields["platformGatewayId"].Enabled = CanEdit() && platform == MqttPlatform.ThingsPanel;
    }

    private GatewayConfiguration CloudDraft => _cloudDraft ??= Clone(_configuration);

    private void ResetCloudDraft()
    {
        _cloudDraft = null;
        _dirty = false;
    }

    private async Task ApplyCloudSettingsAsync()
    {
        if (_application.IsRunning || _closing) throw new InvalidOperationException("请先停止全部运行设备，再应用平台设置。");
        if (!_dirty || _cloudDraft is null) return;
        // Device changes may have been committed since this draft was opened.
        // Apply only global/platform settings to the latest saved device collection.
        var candidate = Clone(_configuration);
        candidate.GatewayId = _cloudDraft.GatewayId;
        candidate.Name = _cloudDraft.Name;
        candidate.AutoStart = _cloudDraft.AutoStart;
        candidate.MaxOutboxMessages = _cloudDraft.MaxOutboxMessages;
        candidate.PublishIntervalMs = _cloudDraft.PublishIntervalMs;
        candidate.ReportMode = _cloudDraft.ReportMode;
        candidate.HeartbeatIntervalMs = _cloudDraft.HeartbeatIntervalMs;
        candidate.Mqtt = Clone(_cloudDraft.Mqtt);
        var options = await ConfirmConfigurationChangeAsync(candidate);
        var applied = await _application.ApplyGlobalSettingsAsync(_cloudDraft, options);
        CompleteConfigurationCommit(applied, successMessage: "平台与系统设置已应用并自动保存。", replaceCloudDraft: true);
    }

    private void BindCloudFields()
    {
        if (_cloudFields.Count == 0 || _dirty) return;
        _binding = true;
        try
        {
            void Text(string key, string value) => ((TextBox)_cloudFields[key]).Text = value;
            void Number(string key, int value) { var field = (NumericUpDown)_cloudFields[key]; field.Value = Math.Clamp(value, (int)field.Minimum, (int)field.Maximum); }
            void Check(string key, bool value) => ((CheckBox)_cloudFields[key]).Checked = value;
            void Choice(string key, object value) => ((ComboBox)_cloudFields[key]).SelectedItem = value;
            Text("gatewayId", _configuration.GatewayId); Text("name", _configuration.Name); Check("autoStart", _configuration.AutoStart);
            Number("maxOutbox", _configuration.MaxOutboxMessages);
            Check("mqttEnabled", _configuration.Mqtt.Enabled); Text("host", _configuration.Mqtt.Host); Number("port", _configuration.Mqtt.Port);
            Check("tls", _configuration.Mqtt.UseTls); Text("clientId", _configuration.Mqtt.ClientId); Text("username", _configuration.Mqtt.Username);
            Text("password", _configuration.Mqtt.Password); Text("topic", _configuration.Mqtt.Topic);
            Number("publishInterval", _configuration.PublishIntervalMs); Number("mqttTimeout", _configuration.Mqtt.TimeoutMs);
            Choice("platform", _configuration.Mqtt.Platform); Choice("reportMode", _configuration.ReportMode);
            Number("heartbeatInterval", _configuration.HeartbeatIntervalMs);
            Text("accessToken", _configuration.Mqtt.AccessToken);
            Text("deviceType", _configuration.Mqtt.PlatformDeviceType); Text("typeKey", _configuration.Mqtt.ThingsCloudTypeKey);
            Text("platformGatewayId", _configuration.Mqtt.PlatformGatewayId);
            Check("rpcEnabled", _configuration.Mqtt.RpcEnabled); Check("remoteWrite", _configuration.Mqtt.AllowRemoteWrite);
            Number("rpcTimeout", _configuration.Mqtt.RpcTimeoutMs);
            Text("rpcRequest", _configuration.Mqtt.RpcRequestTopic); Text("rpcResponse", _configuration.Mqtt.RpcResponseTopic);
        }
        finally { _binding = false; }
    }
}
