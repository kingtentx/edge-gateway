using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly Dictionary<string, Control> _opcUaFields = [];
    private readonly Dictionary<string, TextBox> _opcUaStatusFields = [];
    private OpcUaServerConfiguration? _opcUaDraft;
    private bool _opcUaDirty, _bindingOpcUa;
    private string? _opcUaUiError;
    private Button _opcUaStart = null!, _opcUaStop = null!, _opcUaApply = null!, _opcUaReset = null!;
    private Button _opcUaCertificates = null!, _opcUaCopyEndpoint = null!, _opcUaExportCertificate = null!;

    private Control BuildOpcUaPage()
    {
        var viewport = new EditorLayout.FieldViewport(190) { Name = "OpcUaFieldsViewport" };
        var fields = viewport.Table;
        fields.Name = "OpcUaFields";
        void Section(string caption)
        {
            var row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heading = EditorLayout.WrappingLabel(caption, 12, UiTheme.Blue, true);
            heading.Padding = new Padding(0, 12, 0, 6);
            fields.Controls.Add(heading, 0, row); fields.SetColumnSpan(heading, 2);
        }
        void Status(string key, string caption, bool multiline = false)
        {
            var input = new TextBox
            {
                Name = "OpcUaStatus_" + key, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White, ForeColor = UiTheme.Ink, Multiline = multiline,
                Height = multiline ? 76 : 30, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
            };
            _opcUaStatusFields.Add(key, input); EditorDialog.Field(fields, caption, input);
        }
        void AddInput(string key, string caption, Control input)
        {
            input.Name = "OpcUaField_" + key;
            _opcUaFields.Add(key, input); _configurationCommands.Add(input);
            EditorDialog.Field(fields, caption, input);
        }
        void TextField(string key, string caption, Action<string> update)
        {
            var input = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            input.TextChanged += (_, _) => { if (!_bindingOpcUa) { update(input.Text); MarkOpcUaDirty(); } };
            AddInput(key, caption, input);
        }
        void CheckField(string key, string caption, string text, Action<bool> update)
        {
            var input = new CheckBox { Text = text, AutoSize = true, MinimumSize = new Size(90, 32) };
            input.CheckedChanged += (_, _) => { if (!_bindingOpcUa) { update(input.Checked); MarkOpcUaDirty(); } };
            AddInput(key, caption, input);
        }
        void NumberField(string key, string caption, int minimum, int maximum, Action<int> update)
        {
            var input = new NumericUpDown { Minimum = minimum, Maximum = maximum, ThousandsSeparator = true, BorderStyle = BorderStyle.FixedSingle };
            input.ValueChanged += (_, _) => { if (!_bindingOpcUa) { update((int)input.Value); MarkOpcUaDirty(); } };
            AddInput(key, caption, input);
        }

        Section("服务运行状态");
        Status("state", "已保存许可 / 实际状态");
        Status("endpoint", "已保存的访问端点");
        Status("namespace", "命名空间 URI");
        Status("counts", "节点 / 已连接会话");
        Status("started", "本次启动时间");
        Status("thumbprint", "服务证书指纹");
        Status("directory", "证书目录");
        Status("error", "最近服务错误", multiline: true);

        Section("服务端点与身份验证");
        CheckField("enabled", "允许并自动启动服务", "启用", x => OpcUaDraft.Enabled = x);
        TextField("endpoint", "OPC UA TCP 端点", x => OpcUaDraft.EndpointUrl = x);
        CheckField("anonymous", "匿名访问", "只读", x => OpcUaDraft.AllowAnonymous = x);
        CheckField("userAuthentication", "用户名验证", "启用", x => OpcUaDraft.EnableUserAuthentication = x);
        TextField("username", "用户名", x => OpcUaDraft.Username = x);
        TextField("passwordEnv", "密码环境变量名称", x => OpcUaDraft.PasswordEnvironmentVariable = x);
        CheckField("remoteWrite", "允许远程写入", "允许", x => OpcUaDraft.AllowRemoteWrite = x);
        EditorDialog.Hint(fields, "仅提供 Basic256Sha256 / SignAndEncrypt 安全端点，并验证客户端证书。匿名会话始终只读。远程写入还要求用户名验证、点位允许写入且所属设备正在采集；表达式点只读。密码填写环境变量名称，实际密码不写入配置。");

        Section("更新与会话限制");
        NumberField("updateInterval", "地址空间更新周期 (ms)", 50, 60_000, x => OpcUaDraft.UpdateIntervalMs = x);
        NumberField("maxSessions", "最大并发会话数", 1, 200, x => OpcUaDraft.MaxSessions = x);
        NumberField("operationTimeout", "操作超时 (ms)", 100, 60_000, x => OpcUaDraft.OperationTimeoutMs = x);
        EditorDialog.Hint(fields, "应用设置会按已保存许可启动或重新启动 OPC UA 服务，已有客户端需重连。手动停止不改变启用设置；下次打开程序仍按保存的设置自动启动。服务启停与证书操作不停止设备采集。");

        _opcUaApply = ConfigurationButton("应用设置", async (_, _) => await RunBusyAsync(ApplyOpcUaSettingsAsync), true);
        _opcUaApply.Name = "ApplyOpcUaSettings";
        _stoppedConfigurationCommands.Add(_opcUaApply);
        _opcUaReset = ConfigurationButton("重置修改", (_, _) =>
        {
            if (!CanEdit()) return;
            ResetOpcUaDraft(); BindOpcUaFields(); UpdateCommands();
            SetFooter("OPC UA设置已恢复为上次保存的内容。");
        });
        _opcUaReset.Name = "ResetOpcUaSettings";
        _opcUaStart = UiTheme.Button("启动服务", async (_, _) => await RunBusyAsync(() => StartOpcUaServiceAsync()));
        _opcUaStart.Name = "StartOpcUaService";
        _opcUaStop = UiTheme.Button("停止服务", async (_, _) => await RunBusyAsync(StopOpcUaServiceAsync));
        _opcUaStop.Name = "StopOpcUaService";
        _opcUaCertificates = UiTheme.Button("证书管理", async (_, _) => await RunBusyAsync(ManageOpcUaCertificatesAsync));
        _opcUaCertificates.Name = "ManageOpcUaCertificates";
        _opcUaCopyEndpoint = UiTheme.Button("复制端点", (_, _) =>
        {
            try { Clipboard.SetText(_opcUaServer.GetStatus().EndpointUrl); SetFooter("已复制保存的 OPC UA访问端点。"); }
            catch (Exception exception) { ShowError("复制端点失败：" + exception.Message); }
        });
        _opcUaCopyEndpoint.Name = "CopyOpcUaEndpoint";
        _opcUaExportCertificate = UiTheme.Button("导出公共证书", async (_, _) => await RunBusyAsync(ExportOpcUaServerCertificateAsync));
        _opcUaExportCertificate.Name = "ExportOpcUaCertificate";
        return UiTheme.PageLayout("OPC UA服务", "设备数据可由 OPC UA客户端浏览、订阅和受控写入。草稿可随时编辑，停止全部设备后应用设置。",
            UiTheme.Toolbar(_opcUaApply, _opcUaReset, _opcUaStart, _opcUaStop, _opcUaCertificates, _opcUaCopyEndpoint, _opcUaExportCertificate), viewport);
    }

    private OpcUaServerConfiguration OpcUaDraft => _opcUaDraft ??= Clone(_configuration.OpcUa);
    private void MarkOpcUaDirty() { _opcUaDirty = true; UpdateCommands(); }
    private void ResetOpcUaDraft() { _opcUaDraft = null; _opcUaDirty = false; }

    private void BindOpcUaFields()
    {
        if (_opcUaFields.Count == 0) return;
        if (!_opcUaDirty)
        {
            _bindingOpcUa = true;
            try
            {
                var saved = _configuration.OpcUa;
                void Check(string key, bool value) => ((CheckBox)_opcUaFields[key]).Checked = value;
                void Text(string key, string value) => ((TextBox)_opcUaFields[key]).Text = value;
                void Number(string key, int value)
                {
                    var input = (NumericUpDown)_opcUaFields[key];
                    input.Value = Math.Clamp(value, (int)input.Minimum, (int)input.Maximum);
                }
                Check("enabled", saved.Enabled); Text("endpoint", saved.EndpointUrl);
                Check("anonymous", saved.AllowAnonymous); Check("userAuthentication", saved.EnableUserAuthentication);
                Text("username", saved.Username); Text("passwordEnv", saved.PasswordEnvironmentVariable);
                Check("remoteWrite", saved.AllowRemoteWrite); Number("updateInterval", saved.UpdateIntervalMs);
                Number("maxSessions", saved.MaxSessions); Number("operationTimeout", saved.OperationTimeoutMs);
            }
            finally { _bindingOpcUa = false; }
        }
        RefreshOpcUaStatus();
    }

    private async Task ApplyOpcUaSettingsAsync()
    {
        if (_runtime.IsRunning || _closing) throw new InvalidOperationException("请先停止全部运行设备，再应用 OPC UA设置。");
        if (!_opcUaDirty || _opcUaDraft is null) return;
        // Drafts never replace devices or separately pending platform settings.
        var candidate = Clone(_configuration);
        candidate.OpcUa = Clone(_opcUaDraft);
        await CommitConfigurationAsync(candidate, successMessage: "OPC UA设置已应用并自动保存。");
        ResetOpcUaDraft(); BindOpcUaFields(); UpdateCommands();
    }

    private async Task<string?> ReconfigureOpcUaAfterCommitAsync()
    {
        try { await _opcUaServer.ReconfigureAsync(); _opcUaUiError = null; return null; }
        catch (Exception exception)
        {
            _opcUaUiError = exception.Message;
            _log.Write(GatewayLogLevel.Error, "OPC UA设置已保存，服务应用失败：" + exception.Message);
            return exception.Message;
        }
        finally { RefreshOpcUaStatus(); }
    }

    private async Task StartOpcUaServiceAsync(bool showError = true)
    {
        try
        {
            if (_closing) return;
            await _opcUaServer.StartAsync(); _opcUaUiError = null;
            SetFooter("OPC UA服务已启动，客户端需使用安全端点并信任服务公共证书。");
        }
        catch (Exception exception)
        {
            _opcUaUiError = exception.Message;
            _log.Write(GatewayLogLevel.Error, "OPC UA服务启动失败：" + exception.Message);
            SetFooter("OPC UA服务未启动：" + exception.Message);
            if (_smokeTest) throw;
            if (showError) ShowError("OPC UA服务未启动：" + exception.Message);
        }
        finally { RefreshOpcUaStatus(); UpdateCommands(); }
    }

    private async Task StopOpcUaServiceAsync()
    {
        try { await _opcUaServer.StopAsync(); SetFooter("OPC UA服务已停止，设备采集保持原状态。"); }
        finally { RefreshOpcUaStatus(); UpdateCommands(); }
    }

    private void RefreshOpcUaStatus()
    {
        if (_opcUaStatusFields.Count == 0 || IsDisposed) return;
        var status = _opcUaServer.GetStatus();
        void Set(string key, string value)
        {
            var field = _opcUaStatusFields[key];
            if (field.Text != value) field.Text = value;
        }
        Set("state", (status.Enabled ? "允许启动" : "未启用") + " / " + (status.IsRunning ? "服务运行中" : "服务已停止"));
        _opcUaStatusFields["state"].ForeColor = status.IsRunning ? UiTheme.Green : UiTheme.Muted;
        Set("endpoint", status.EndpointUrl); Set("namespace", status.NamespaceUri);
        Set("counts", $"{status.NodeCount:N0} 个节点 / {status.SessionCount:N0} 个会话");
        Set("started", status.StartedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—");
        Set("thumbprint", status.CertificateThumbprint ?? "首次启动后生成");
        Set("directory", status.CertificateDirectory);
        Set("error", _opcUaUiError ?? status.LastError ?? "无");
        _opcUaStatusFields["error"].ForeColor = _opcUaUiError is null && status.LastError is null ? UiTheme.Muted : UiTheme.Red;
        UpdateOpcUaCommands();
    }

    private void UpdateOpcUaCommands()
    {
        if (_opcUaStart is null) return;
        var status = _opcUaServer.GetStatus();
        var available = !_busy && !_closing;
        _opcUaStart.Enabled = available && status.Enabled && !status.IsRunning;
        _opcUaStop.Enabled = available && status.IsRunning;
        _opcUaApply.Enabled = available && !_runtime.IsRunning;
        _opcUaReset.Enabled = available;
        _opcUaCertificates.Enabled = _opcUaCopyEndpoint.Enabled = _opcUaExportCertificate.Enabled = available;
    }

    private async Task ManageOpcUaCertificatesAsync()
    {
        using var dialog = new OpcUaCertificatesDialog(_opcUaServer, _log);
        await dialog.RefreshCertificatesAsync();
        dialog.ShowDialog(this);
        RefreshOpcUaStatus();
    }

    private async Task ExportOpcUaServerCertificateAsync()
    {
        var certificate = await _opcUaServer.ExportServerCertificateAsync();
        using var dialog = new SaveFileDialog
        {
            Title = "导出 OPC UA服务公共证书", Filter = "DER 公共证书 (*.der)|*.der|CER 公共证书 (*.cer)|*.cer",
            FileName = "edge-gateway-opcua-server.der", OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await File.WriteAllBytesAsync(dialog.FileName, certificate);
        SetFooter("已导出 OPC UA服务公共证书，可导入客户端信任列表。");
    }
}
