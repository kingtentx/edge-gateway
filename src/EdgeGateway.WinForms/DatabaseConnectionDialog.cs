using EdgeGateway.Application;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal sealed partial class DatabaseConnectionDialog : EditorDialog
{
    private readonly DatabaseConnectionConfiguration _source;
    private readonly IGatewayApplication _application;
    private bool _testing;
    internal DatabaseConnectionConfiguration Value { get; private set; } = null!;
    internal IEnumerable<Control> SmokeInputs => new Control[] { _name, _enabled, _provider, _host, _port, _database, _username, _password, _tls, _connectTimeout, _commandTimeout, _batch, _flush, _capacity, _capacityMb, _retryAttempts };

    public DatabaseConnectionDialog() : base("数据库连接", "配置外部数据库连接与批次。")
    { _source = new(); _application = null!; InitializeComponent(); }

    internal DatabaseConnectionDialog(DatabaseConnectionConfiguration configuration, IGatewayApplication application)
        : base("数据库连接", "配置采集数据的外部数据库；直接输入密码并随连接配置保存。保存前需停止全部设备。")
    {
        _source = GatewayJson.Clone(configuration); _application = application;
        InitializeComponent();
        _name.Text = _source.Name;
        _enabled.Checked = _source.Enabled;
        _host.Text = _source.Host;
        _port.Value = _source.Port;
        _database.Text = _source.Database;
        _username.Text = _source.Username;
        _password.Text = _source.Password;
        _tls.Checked = _source.UseTls;
        _connectTimeout.Value = Math.Clamp(_source.ConnectionTimeoutSeconds, 1, 120);
        _commandTimeout.Value = Math.Clamp(_source.CommandTimeoutSeconds, 1, 300);
        _batch.Value = Math.Clamp(_source.BatchSize, 1, 5000);
        _flush.Value = Math.Clamp(_source.FlushIntervalMs, 50, 60000);
        _capacity.Value = Math.Clamp(_source.QueueCapacityRows, 1, 1000000);
        _capacityMb.Value = Math.Clamp(_source.QueueCapacityMb, 1, 1024);
        _retryAttempts.Value = Math.Clamp(_source.MaxRetryAttempts, 1, 10);
        _provider.Items.AddRange(Enum.GetValues<ExternalDatabaseProvider>().Cast<object>().ToArray());
        _provider.SelectedItem = _source.Provider;
        _provider.Format += (_, e) => { if (e.ListItem is ExternalDatabaseProvider value) e.Value = DatabaseUiText.Provider(value); };
        testConnectionButton.Click += async (_, _) => await TestAsync();
        _provider.SelectedIndexChanged += (_, _) =>
        {
            if (_provider.SelectedItem is not ExternalDatabaseProvider selected) return;
            if (_port.Value is 3306 or 5432 or 1521) _port.Value = selected switch { ExternalDatabaseProvider.MySql => 3306, ExternalDatabaseProvider.PostgreSql => 5432, _ => 1521 };
        };
    }

    private DatabaseConnectionConfiguration CaptureConnectionDraft()
    {
        var value = GatewayJson.Clone(_source);
        value.Name = Required(_name, "连接名称"); value.Host = Required(_host, "服务器"); value.Database = Required(_database, "数据库 / 服务名");
        value.Username = Required(_username, "用户名"); value.Password = _password.Text;
        value.Provider = (ExternalDatabaseProvider)_provider.SelectedItem!; value.Port = (int)_port.Value; value.Enabled = _enabled.Checked; value.UseTls = _tls.Checked;
        value.ConnectionTimeoutSeconds = (int)_connectTimeout.Value; value.CommandTimeoutSeconds = (int)_commandTimeout.Value;
        value.BatchSize = (int)_batch.Value; value.FlushIntervalMs = (int)_flush.Value; value.QueueCapacityRows = (int)_capacity.Value; value.QueueCapacityMb = (int)_capacityMb.Value; value.MaxRetryAttempts = (int)_retryAttempts.Value;
        return value;
    }

    private async Task TestAsync()
    {
        if (_testing) return;
        try
        {
            var connection = CaptureConnectionDraft(); _testing = true; Tabs.Enabled = false; UseWaitCursor = true;
            _testResult.Text = "正在测试连接并检查默认数据表…"; _testResult.ForeColor = UiTheme.Muted;
            var result = await _application.TestDatabaseConnectionAsync(connection);
            _testResult.Text = result.Message + (string.IsNullOrWhiteSpace(result.ServerVersion) ? "" : "\n服务器版本：" + result.ServerVersion);
            _testResult.ForeColor = result.Success ? UiTheme.Green : UiTheme.Red;
        }
        catch (Exception exception) { _testResult.Text = exception.Message; _testResult.ForeColor = UiTheme.Red; }
        finally { _testing = false; Tabs.Enabled = true; UseWaitCursor = false; }
    }
    protected override void Commit() { if (_testing) throw new InvalidOperationException("连接测试尚未完成，请稍后保存。"); Value = CaptureConnectionDraft(); }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (_testing) e.Cancel = true; base.OnFormClosing(e); }
    internal DatabaseConnectionConfiguration CommitForSmoke() { Commit(); return Value; }
    internal static void Add(TableLayoutPanel fields, string name, string label, Control control) { control.Name = name; Field(fields, label, control); }
    internal static void Span(TableLayoutPanel fields, Control control)
    { var row = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize)); fields.Controls.Add(control, 0, row); fields.SetColumnSpan(control, 2); }
}
