using EdgeGateway.Application;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly Dictionary<string, Control> _cloudFields = [];
    private GatewayConfiguration? _cloudDraft;
    private bool _binding;
    private readonly TabControl _cloudTabs = new EditorLayout.EditorTabs { Name = "PlatformTabs", Dock = DockStyle.Fill, Multiline = true, Margin = Padding.Empty };
    private readonly Label _cloudApplyState = UiTheme.Label("设置已应用", 10, UiTheme.Muted);
    private Button _cloudApply = null!, _cloudReset = null!;
    private Control BuildCloudPage()
    {
        var content = new TableLayoutPanel
        {
            Name = "PlatformContent", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            BackColor = Color.White, Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tables = new Dictionary<string, TableLayoutPanel>();
        TableLayoutPanel Page(string key, string title)
        {
            var tab = new TabPage(title) { Name = "PlatformTab_" + key, BackColor = Color.White };
            // Static page fields inherit the main form's DPI. A plain scroll panel
            // avoids ContainerControl active-child scrolling fighting the page viewport.
            var viewport = new Panel
            {
                Name = "PlatformFields_" + key, Dock = DockStyle.Fill, AutoScroll = true,
                BackColor = Color.White, Padding = new Padding(12), Margin = Padding.Empty
            };
            var table = EditorLayout.Fields(); table.Name = "PlatformTable_" + key;
            viewport.Controls.Add(table); tab.Controls.Add(viewport); _cloudTabs.TabPages.Add(tab);
            tables.Add(key, table);
            return table;
        }
        var fields = Page("connection", "平台连接");
        void Section(string title, string? help = null) => EditorDialog.Section(fields, title, help);
        void Note(string text) => EditorLayout.FieldNote(fields, text);
        void AddField(string key, string title, Control input)
        {
            input.Name = "CloudField_" + key;
            EditorDialog.Field(fields, title, input);
            _cloudFields.Add(key, input); _configurationCommands.Add(input);
        }
        void TextField(string key, string title, Action<string> update, string? placeholder = null, bool secret = false)
        {
            var input = new TextBox { PlaceholderText = placeholder ?? "", UseSystemPasswordChar = secret };
            input.TextChanged += (_, _) => { if (!_binding) { update(input.Text); MarkDirty(); } };
            AddField(key, title, input);
        }
        void NumberField(string key, string title, int min, int max, Action<int> update)
        {
            var input = new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, ThousandsSeparator = true };
            input.ValueChanged += (_, _) => { if (!_binding) { update((int)input.Value); MarkDirty(); } };
            AddField(key, title, input);
        }
        void CheckField(string key, string title, Action<bool> update)
        {
            var input = new CheckBox { Text = "启用", AutoSize = true };
            input.CheckedChanged += (_, _) => { if (!_binding) { update(input.Checked); MarkDirty(); } };
            AddField(key, title, input);
        }
        void ChoiceField<T>(string key, string title, Action<T> update, Func<T, string>? display = null) where T : struct, Enum
        {
            var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FormattingEnabled = true };
            EditorLayout.ConfigureDropDown(input, 520, 320);
            input.Items.AddRange(Enum.GetValues<T>().Cast<object>().ToArray());
            if (display is not null) input.Format += (_, e) => { if (e.ListItem is T value) e.Value = display(value); };
            input.SelectedIndexChanged += (_, _) => { if (!_binding && input.SelectedItem is T value) { update(value); MarkDirty(); } };
            AddField(key, title, input);
        }
        Section("连接平台", "先选择平台，再填写连接地址与凭据。未启用上报时也可预先编辑这些设置。");
        CheckField("mqttEnabled", "MQTT 上报", x => CloudDraft.Mqtt.Enabled = x);
        ChoiceField<MqttPlatform>("platform", "接入平台", x => CloudDraft.Mqtt.Platform = x);
        TextField("host", "Broker 主机", x => CloudDraft.Mqtt.Host = x, "例如 192.168.1.10 或 mqtt.example.com");
        NumberField("port", "Broker 端口", 1, 65535, x => CloudDraft.Mqtt.Port = x);
        CheckField("tls", "TLS 加密连接", x => CloudDraft.Mqtt.UseTls = x);
        TextField("clientId", "Client ID", x => CloudDraft.Mqtt.ClientId = x);
        NumberField("mqttTimeout", "连接 / 发送超时 (ms)", 500, 120_000, x => CloudDraft.Mqtt.TimeoutMs = x);
        Section("身份与凭据", "直接输入用户名、密码或平台 Token，随配置保存。");
        TextField("username", "用户名", x => CloudDraft.Mqtt.Username = x);
        TextField("accessToken", "平台 Token", x => CloudDraft.Mqtt.AccessToken = x, "平台使用 Token 作为用户名时填写", secret: true);
        TextField("password", "密码", x => CloudDraft.Mqtt.Password = x, secret: true);
        Note("平台 Token 非空时作为 MQTT 用户名；TLS 使用系统信任链验证证书。");
        Section("平台参数", "仅当前平台使用的专有字段可编辑；切换平台保留已填写的草稿。");
        TextField("topic", "通用 MQTT 上报主题", x => CloudDraft.Mqtt.Topic = x);
        Note("通用 MQTT 支持 {gatewayId}；专用平台自动生成主题和报文。");
        TextField("deviceType", "平台设备类型", x => CloudDraft.Mqtt.PlatformDeviceType = x);
        TextField("typeKey", "ThingsCloud 类型标识", x => CloudDraft.Mqtt.ThingsCloudTypeKey = x);
        TextField("platformGatewayId", "ThingsPanel 网关编号", x => CloudDraft.Mqtt.PlatformGatewayId = x);

        fields = Page("reporting", "上报策略");
        Section("数据发送策略", "采集与上报分别控制。点位可关闭上传，同时保留本地采集、采样日志和已配置的数据库写入。");
        ChoiceField<TelemetryReportMode>("reportMode", "上报模式", x => CloudDraft.ReportMode = x,
            x => x switch { TelemetryReportMode.OnChange => "仅变化上报", TelemetryReportMode.OnChangeWithHeartbeat => "变化上报 + 周期心跳", _ => "周期全量上报" });
        NumberField("publishInterval", "上报 / 检查周期 (ms)", 100, 3_600_000, x => CloudDraft.PublishIntervalMs = x);
        NumberField("heartbeatInterval", "心跳周期 (ms)", 100, 86_400_000, x => CloudDraft.HeartbeatIntervalMs = x);
        EditorDialog.Hint(fields, "变化死区在点位配置中设置。配置或设备重启后重新发送首帧；心跳保留原采样时间。");
        Section("离线补传");
        NumberField("maxOutbox", "最大待发送消息数", 1, 1_000_000, x => CloudDraft.MaxOutboxMessages = x);
        EditorDialog.Hint(fields, "消息持久化后发送，确认成功才移出队列。当前积压和重试详情可在“通信诊断”查看。");

        fields = Page("rpc", "远程控制");
        Section("远程读写权限", "只有已运行、已启用的点位可接受请求；写入还要求该点位具有写权限。");
        CheckField("rpcEnabled", "接收 RPC 请求", x => CloudDraft.Mqtt.RpcEnabled = x);
        CheckField("remoteWrite", "允许远程写入", x => CloudDraft.Mqtt.AllowRemoteWrite = x);
        NumberField("rpcTimeout", "RPC 命令超时 (ms)", 500, 60_000, x => CloudDraft.Mqtt.RpcTimeoutMs = x);
        Section("通用 MQTT 主题", "专用平台使用各自协议的请求和响应主题。");
        TextField("rpcRequest", "RPC 请求主题", x => CloudDraft.Mqtt.RpcRequestTopic = x);
        TextField("rpcResponse", "RPC 回复主题", x => CloudDraft.Mqtt.RpcResponseTopic = x);

        fields = Page("gateway", "网关设置");
        Section("网关身份");
        TextField("gatewayId", "网关标识", x => CloudDraft.GatewayId = x);
        TextField("name", "网关名称", x => CloudDraft.Name = x);
        CheckField("autoStart", "程序启动后自动采集", x => CloudDraft.AutoStart = x);
        Section("驱动插件", "查看全部驱动及引用设备；内置与上传驱动均支持详情、更新 DLL 和删除，也可新增驱动。");
        var manageDrivers = UiTheme.Button("驱动插件管理", (_, _) =>
        {
            using var dialog = new DriverPluginDialog(_application, _driverDirectory);
            dialog.ShowDialog(this);
            RebindConfigurationViews();
        }, true);
        manageDrivers.Name = "ManageDriverPlugins";
        DatabaseConnectionDialog.Span(fields, manageDrivers);

        _cloudApply = ConfigurationButton("应用设置", async (_, _) => await RunBusyAsync(ApplyCloudSettingsAsync), true);
        _cloudApply.Name = "ApplyCloudSettings"; _stoppedConfigurationCommands.Add(_cloudApply);
        _cloudReset = ConfigurationButton("重置修改", (_, _) =>
        {
            if (!CanEdit()) return;
            ResetCloudDraft(); BindCloudFields(); UpdateCommands();
            SetFooter("平台设置已恢复为上次保存的内容。");
        });
        _cloudReset.Name = "ResetCloudSettings";
        var footer = new TableLayoutPanel
        {
            Name = "PlatformApplyBar", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(14, 8, 8, 4), Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _cloudApplyState.Name = "PlatformDraftState"; _cloudApplyState.Dock = DockStyle.Fill;
        footer.Controls.Add(_cloudApplyState, 0, 0);
        var actions = UiTheme.Toolbar(_cloudApply, _cloudReset); actions.Padding = Padding.Empty;
        footer.Controls.Add(actions, 0, 1);
        footer.SizeChanged += (_, _) => SetWrappingWidth(_cloudApplyState, footer.ClientSize.Width - footer.Padding.Horizontal);
        content.Controls.Add(_cloudTabs, 0, 0); content.Controls.Add(footer, 0, 1);
        return UiTheme.PageLayout("平台与系统", "分组维护连接、上报、远程控制和网关设置；编辑完成后统一应用。", UiTheme.Toolbar(), content);
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
