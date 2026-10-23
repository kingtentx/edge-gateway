using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.OpcUa;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EdgeGateway.WinForms;

internal sealed class OpcUaCertificatesDialog : EditorDialog
{
    private readonly GatewayOpcUaServer _server;
    private readonly IGatewayLog _log;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly ComboBox _filter = new() { Name = "CertificateStoreFilter", DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly ComboBox _selected = new() { Name = "SelectedClientCertificate", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _details = new()
    {
        Name = "ClientCertificateDetails", ReadOnly = true, Multiline = true, Height = 100,
        ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White
    };
    private readonly Button _refresh, _import, _trust, _remove, _regenerate;
    private readonly Label _result;
    private IReadOnlyList<OpcUaCertificateInfo> _certificates = [];
    private bool _working, _binding;

    internal OpcUaCertificatesDialog(GatewayOpcUaServer server, IGatewayLog log)
        : base("OPC UA证书管理", "检查客户端公共证书的来源、指纹和有效期，再明确授予或撤销信任。匿名身份同样需要通过安全通道的证书验证。")
    {
        _server = server; _log = log;
        SetReadOnlyView();
        _filter.Items.AddRange(new object[] { "全部客户端证书", "已信任", "已拒绝 / 待核对" });
        _filter.SelectedIndex = 0;
        EditorLayout.ConfigureDropDown(_filter, 300, 200);
        EditorLayout.ConfigureDropDown(_selected, 680, 320);
        _filter.SelectedIndexChanged += (_, _) => BindGrid();
        _selected.SelectedIndexChanged += (_, _) => { if (!_binding) UpdateSelection(); };

        _refresh = UiTheme.Button("刷新列表", async (_, _) => await RunCertificateOperationAsync(RefreshCertificatesAsync));
        _refresh.Name = "RefreshClientCertificates";
        _grid.Name = "ClientCertificateGrid";
        UiTheme.AddColumn(_grid, "StoreText", "状态", 90, 70);
        UiTheme.AddColumn(_grid, "Subject", "证书主体", 220, 180);
        UiTheme.AddColumn(_grid, "Expires", "有效期至", 160, 100);
        UiTheme.AddColumn(_grid, "Thumbprint", "指纹", 300, 160);
        _grid.SelectionChanged += (_, _) =>
        {
            if (_binding || _grid.CurrentRow?.DataBoundItem is not CertificateRow row) return;
            _selected.SelectedItem = _selected.Items.Cast<CertificateChoice>().FirstOrDefault(x =>
                x.Info.Store == row.Info.Store && x.Info.Thumbprint == row.Info.Thumbprint);
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Tabs.SelectedIndex = 1; };
        var listPage = new TabPage("客户端证书") { BackColor = Color.White, Padding = new Padding(8) };
        var list = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        list.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = UiTheme.Toolbar(_filter, _refresh); toolbar.Padding = Padding.Empty;
        _filter.Margin = new Padding(0, 7, 8, 7);
        list.Controls.Add(toolbar, 0, 0); list.Controls.Add(_grid, 0, 1);
        listPage.Controls.Add(list); Tabs.TabPages.Add(listPage);

        var management = AddTab("信任管理");
        Field(management, "选择单张证书", _selected);
        Field(management, "主体、指纹与有效期", _details);
        _import = UiTheme.Button("导入公共证书", async (_, _) => await RunCertificateOperationAsync(ImportCertificateAsync));
        _import.Name = "ImportClientCertificate";
        _trust = UiTheme.Button("信任所选证书", async (_, _) => await RunCertificateOperationAsync(TrustSelectedAsync), true);
        _trust.Name = "TrustSelectedCertificate";
        _remove = UiTheme.Button("移除所选信任", async (_, _) => await RunCertificateOperationAsync(RemoveSelectedAsync));
        _remove.Name = "RemoveTrustedCertificate";
        _regenerate = UiTheme.Button("重建服务端证书", async (_, _) => await RunCertificateOperationAsync(RegenerateServerCertificateAsync));
        _regenerate.Name = "RegenerateOpcUaServerCertificate";
        Field(management, "从文件导入", _import);
        Field(management, "已拒绝证书", _trust);
        Field(management, "已信任证书", _remove);
        Field(management, "服务证书主机名变更", _regenerate);
        _result = EditorLayout.WrappingLabel("尚未载入证书列表。", 10, UiTheme.Muted);
        var rowIndex = management.RowCount++; management.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        management.Controls.Add(_result, 0, rowIndex); management.SetColumnSpan(_result, 2);
        Hint(management, "仅接受单张 DER / CER 或公共 PEM 证书，不导入私钥、PFX / P12 或证书集合。移除信任会重启 OPC UA端点并断开现有客户端，设备采集继续。信任证书不授予匿名写权限。端点主机名或 IP 变更时，先应用设置，再重建服务端证书并重新导出供客户端信任。");
        UpdateSelection();
    }

    internal DataGridView SmokeCertificateGrid => _grid;
    internal IReadOnlyList<Control> SmokeCertificateCommands => new Control[] { _refresh, _import, _trust, _remove, _regenerate };
    internal IReadOnlyList<Control> SmokeManagementInputs => new Control[] { _selected, _details };

    protected override void Commit() { }

    internal async Task RefreshCertificatesAsync()
    {
        var selected = (_selected.SelectedItem as CertificateChoice)?.Info;
        _certificates = await _server.GetCertificatesAsync();
        _binding = true;
        try
        {
            _selected.Items.Clear();
            _selected.Items.AddRange(_certificates.Select(x => new CertificateChoice(x)).Cast<object>().ToArray());
            _selected.SelectedItem = _selected.Items.Cast<CertificateChoice>().FirstOrDefault(x =>
                x.Info.Store == selected?.Store && x.Info.Thumbprint == selected?.Thumbprint);
            if (_selected.SelectedIndex < 0 && _selected.Items.Count > 0) _selected.SelectedIndex = 0;
        }
        finally { _binding = false; }
        BindGrid(); UpdateSelection();
        _result.Text = $"已信任 {_certificates.Count(x => x.Store == OpcUaCertificateStore.Trusted)} 张；已拒绝 {_certificates.Count(x => x.Store == OpcUaCertificateStore.Rejected)} 张。";
        _result.ForeColor = UiTheme.Muted;
    }

    private void BindGrid()
    {
        if (_grid.Columns.Count == 0) return;
        var selected = (_selected.SelectedItem as CertificateChoice)?.Info;
        _binding = true;
        try
        {
            _grid.DataSource = _certificates.Where(x => _filter.SelectedIndex switch
            {
                1 => x.Store == OpcUaCertificateStore.Trusted,
                2 => x.Store == OpcUaCertificateStore.Rejected,
                _ => true
            }).Select(x => new CertificateRow(x)).ToList();
            _grid.ClearSelection();
            foreach (DataGridViewRow row in _grid.Rows)
                if (row.DataBoundItem is CertificateRow value && value.Info.Store == selected?.Store && value.Info.Thumbprint == selected?.Thumbprint)
                { row.Selected = true; _grid.CurrentCell = row.Cells[0]; break; }
        }
        finally { _binding = false; }
    }

    private void UpdateSelection()
    {
        var selected = (_selected.SelectedItem as CertificateChoice)?.Info;
        _details.Text = selected is null ? "暂无证书。可先连接客户端，或导入由客户端提供的公共证书。"
            : $"{StoreText(selected.Store)}\r\n主体：{selected.Subject}\r\n签发者：{selected.Issuer}\r\n指纹：{selected.Thumbprint}\r\n有效期：{selected.NotBefore.ToLocalTime():yyyy-MM-dd HH:mm:ss} 至 {selected.NotAfter.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _trust.Enabled = !_working && selected?.Store == OpcUaCertificateStore.Rejected;
        _remove.Enabled = !_working && selected?.Store == OpcUaCertificateStore.Trusted;
    }

    private async Task ImportCertificateAsync()
    {
        using var file = new OpenFileDialog
        {
            Title = "选择单张客户端公共证书", Filter = "公共证书 (*.der;*.cer;*.crt;*.pem)|*.der;*.cer;*.crt;*.pem",
            CheckFileExists = true, Multiselect = false
        };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(file.FileName).Length > 65_536) throw new InvalidDataException("公共证书文件不得超过 64 KiB。");
        var bytes = await File.ReadAllBytesAsync(file.FileName);
        await ConfirmAndTrustAsync(bytes);
    }

    private async Task TrustSelectedAsync()
    {
        if ((_selected.SelectedItem as CertificateChoice)?.Info is not { Store: OpcUaCertificateStore.Rejected } selected) return;
        var certificate = await _server.ExportCertificateAsync(selected.Store, selected.Thumbprint);
        await ConfirmAndTrustAsync(certificate);
    }

    private async Task ConfirmAndTrustAsync(byte[] bytes)
    {
        using var certificate = ReadPublicCertificateForConfirmation(bytes);
        var message = $"是否信任这张客户端公共证书？\n\n主体：{certificate.Subject}\n签发者：{certificate.Issuer}\n指纹：{certificate.Thumbprint}\n有效期：{certificate.NotBefore:yyyy-MM-dd HH:mm:ss} 至 {certificate.NotAfter:yyyy-MM-dd HH:mm:ss}\n\n请通过可信渠道核对来源与指纹。信任后，持有对应私钥的客户端可建立安全连接；账号与点位权限仍单独验证。";
        if (MessageBox.Show(this, message, "确认信任单张客户端证书", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await _server.TrustCertificateAsync(bytes);
        await RefreshCertificatesAsync();
        _result.Text = "已信任所选公共证书，客户端可重新连接。";
    }

    private async Task RemoveSelectedAsync()
    {
        if ((_selected.SelectedItem as CertificateChoice)?.Info is not { Store: OpcUaCertificateStore.Trusted } selected) return;
        if (MessageBox.Show(this, $"移除此客户端证书的信任？\n\n主体：{selected.Subject}\n指纹：{selected.Thumbprint}\n\nOPC UA端点将重新启动，现有客户端需重新连接。设备采集保持运行。",
                "确认移除指定证书信任", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await _server.RemoveTrustedCertificateAsync(selected.Thumbprint);
        await RefreshCertificatesAsync();
        _result.Text = "已移除所选证书信任，OPC UA客户端需要重新连接。";
    }

    private async Task RegenerateServerCertificateAsync()
    {
        var endpoint = _server.GetStatus().EndpointUrl;
        if (MessageBox.Show(this, $"按当前已应用端点重建服务端证书？\n\n端点：{endpoint}\n\n原服务端证书和私钥将被替换，所有 OPC UA客户端需重新导入并信任新公共证书。运行中的端点会断开并尝试重启；停止状态的端点保持停止。客户端信任列表保留，设备采集继续。",
                "确认重建服务端证书", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await _server.RegenerateServerCertificateAsync();
        await RefreshCertificatesAsync();
        _result.Text = "服务端证书已重建。请关闭窗口后导出新公共证书，供客户端重新信任。";
    }

    private static X509Certificate2 ReadPublicCertificateForConfirmation(byte[] bytes)
    {
        if (bytes.Length is 0 or > 65_536) throw new InvalidDataException("请选择不超过 64 KiB 的单张公共证书。");
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            var trimmed = text.Trim();
            if (!trimmed.StartsWith(begin, StringComparison.Ordinal) || !trimmed.EndsWith(end, StringComparison.Ordinal)
                || trimmed.IndexOf(begin, begin.Length, StringComparison.Ordinal) >= 0
                || trimmed.Contains("PRIVATE KEY", StringComparison.Ordinal))
                throw new InvalidDataException("PEM 文件只能包含单张公共证书，不得包含私钥或证书集合。");
            return X509Certificate2.CreateFromPem(trimmed);
        }
        if (X509Certificate2.GetCertContentType(bytes) != X509ContentType.Cert)
            throw new InvalidDataException("只能导入 DER / CER 公共证书，不能导入 PFX / P12、私钥或证书集合。");
        var certificate = new X509Certificate2(bytes, (string?)null, X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey && bytes.AsSpan().SequenceEqual(certificate.RawData)) return certificate;
        certificate.Dispose();
        throw new InvalidDataException("证书文件只能包含单张公共证书，不得包含私钥或尾随数据。");
    }

    private async Task RunCertificateOperationAsync(Func<Task> operation)
    {
        if (_working) return;
        _working = true; UseWaitCursor = true;
        _refresh.Enabled = _import.Enabled = _regenerate.Enabled = _filter.Enabled = _selected.Enabled = false;
        foreach (var command in SmokeCommands) command.Enabled = false;
        UpdateSelection();
        try { await operation(); }
        catch (Exception exception)
        {
            // Revocation may have succeeded even if restarting the endpoint failed.
            // Reload the actual trust store instead of restoring the old UI selection.
            try { await RefreshCertificatesAsync(); } catch { }
            _result.Text = exception.Message; _result.ForeColor = UiTheme.Red;
            _log.Write(GatewayLogLevel.Error, "OPC UA证书操作失败：" + exception.Message);
            MessageBox.Show(this, exception.Message, "OPC UA证书操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _working = false; UseWaitCursor = false;
            _refresh.Enabled = _import.Enabled = _regenerate.Enabled = _filter.Enabled = _selected.Enabled = true;
            foreach (var command in SmokeCommands) command.Enabled = true;
            UpdateSelection();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_working) e.Cancel = true;
        base.OnFormClosing(e);
    }

    private static string StoreText(OpcUaCertificateStore store) => store == OpcUaCertificateStore.Trusted ? "已信任" : "已拒绝";
    private sealed record CertificateChoice(OpcUaCertificateInfo Info)
    {
        public override string ToString() => $"{StoreText(Info.Store)} · {Info.Subject} · {Info.Thumbprint}";
    }
    private sealed record CertificateRow(OpcUaCertificateInfo Info)
    {
        public string StoreText => OpcUaCertificatesDialog.StoreText(Info.Store);
        public string Subject => Info.Subject;
        public string Expires => Info.NotAfter.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string Thumbprint => Info.Thumbprint;
    }
}
