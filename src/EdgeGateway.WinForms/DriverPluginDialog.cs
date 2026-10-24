using EdgeGateway.Application;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

/// <summary>Local administrator UI for installing trusted, single-file driver factory plugins.</summary>
internal sealed class DriverPluginDialog : DpiAwareForm
{
    private readonly IGatewayApplication _application;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _status = UiTheme.Label("仅安装可信来源的驱动 DLL。驱动代码将在网关进程内执行。", 10, UiTheme.Muted);
    private readonly string _pluginDirectory;

    public DriverPluginDialog(IGatewayApplication application, string dataDirectory)
    {
        _application = application;
        _pluginDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "plugins");
        Text = "驱动插件管理";
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.White;
        ConfigureWindow(new Size(980, 650), new Size(700, 480));
        StartPosition = FormStartPosition.CenterParent;

        UiTheme.AddColumn(_grid, "Id", "驱动标识", 180, 150);
        UiTheme.AddColumn(_grid, "Name", "驱动名称", 200, 160);
        UiTheme.AddColumn(_grid, "Version", "版本", 110, 100);
        UiTheme.AddColumn(_grid, "AssemblyName", "程序集", 180, 140);
        UiTheme.AddColumn(_grid, "Sha256", "SHA-256", 260, 160);
        UiTheme.AddColumn(_grid, "Status", "状态", 90, 80);
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AutoGenerateColumns = false;

        var upload = UiTheme.Button("上传并加载 DLL", async (_, _) => await InstallAsync(), true);
        upload.Name = "UploadDriverDll";
        var open = UiTheme.Button("打开插件目录", (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(_pluginDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_pluginDirectory) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowMessage(ex.Message, MessageBoxIcon.Error); }
        });
        var refresh = UiTheme.Button("刷新驱动列表", (_, _) => RefreshGrid());
        var close = UiTheme.Button("关闭", (_, _) => Close());
        var actions = UiTheme.Toolbar(upload, refresh, open, close);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12), BackColor = Color.White };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status.Dock = DockStyle.Fill;
        layout.Controls.Add(actions, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        Controls.Add(layout);
        Shown += (_, _) => RefreshGrid();
    }

    private async Task InstallAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = "选择已编译的 Edge Gateway 驱动 DLL",
            Filter = "驱动程序集 (*.dll)|*.dll",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var answer = MessageBox.Show(this,
            "DLL 会在当前网关进程内执行，可能访问本机文件、网络和设备。只安装来自可信开发者且已验证的驱动。继续安装？",
            "安全确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        try
        {
            UseWaitCursor = true;
            _status.Text = "正在校验、复制并加载驱动 DLL，请稍候……";
            var sourcePath = picker.FileName;
            var message = await Task.Run(() => _application.InstallDriverPlugin(sourcePath));
            _status.Text = message + Environment.NewLine + "插件已注册；后续打开设备配置即可选择新驱动。";
            RefreshGrid();
            MessageBox.Show(this, message, "驱动安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "安装失败：" + ex.GetBaseException().Message;
            MessageBox.Show(this, _status.Text, "驱动安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }

    private void RefreshGrid()
    {
        var available = _application.Drivers.Drivers.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var installed = _application.GetInstalledDriverPlugins();
        _grid.DataSource = installed.Select(plugin => new
        {
            plugin.Id, plugin.Name, plugin.Version, plugin.AssemblyName, plugin.Sha256, plugin.Status
        }).ToArray();
        if (installed.Count == 0)
            _status.Text = $"当前可用驱动 {available.Count} 个；尚未通过上传安装第三方 DLL。内置驱动不会显示在“已上传插件”列表中。{Environment.NewLine}插件目录：{_pluginDirectory}";
        else
            _status.Text = $"当前可用驱动 {available.Count} 个，上传安装的插件 {installed.Count} 个。{Environment.NewLine}插件目录：{_pluginDirectory}";
    }

    private void ShowMessage(string message, MessageBoxIcon icon) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, icon);
}
