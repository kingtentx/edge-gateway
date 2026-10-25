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
    private bool _refreshing;
    private bool _busy;
    private readonly Dictionary<string, DriverListItem> _drivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _details;
    private readonly Button _update;
    private readonly Button _uninstall;
    private readonly FlowLayoutPanel _actions;

    public DriverPluginDialog(IGatewayApplication application, string driverDirectory)
    {
        _application = application;
        _pluginDirectory = Path.GetFullPath(driverDirectory);
        Text = "驱动插件管理";
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.White;
        ConfigureWindow(new Size(980, 650), new Size(700, 480));
        StartPosition = FormStartPosition.CenterParent;

        UiTheme.AddColumn(_grid, "Name", "驱动名称", 200, 160);
        UiTheme.AddColumn(_grid, "Source", "来源", 110, 100);
        UiTheme.AddColumn(_grid, "Status", "状态", 100, 90);
        UiTheme.AddColumn(_grid, "Id", "驱动标识", 180, 150);
        UiTheme.AddColumn(_grid, "Version", "版本", 110, 100);
        UiTheme.AddColumn(_grid, "AssemblyName", "程序集", 180, 140);
        UiTheme.AddColumn(_grid, "Sha256", "SHA-256", 260, 160);
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AutoGenerateColumns = false;

        var upload = UiTheme.Button("上传并加载 DLL", async (_, _) => await InstallAsync(false), true);
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
        var refresh = UiTheme.Button("刷新驱动列表", async (_, _) => await RefreshGridAsync());
        var close = UiTheme.Button("关闭", (_, _) => Close());
        _details = UiTheme.Button("查看详情", async (_, _) => await ShowDetailsAsync());
        _update = UiTheme.Button("更新 DLL", async (_, _) => await InstallAsync(true));
        _uninstall = UiTheme.Button("删除驱动", async (_, _) => await UninstallAsync());
        _details.Name = "DriverDetails";
        _update.Name = "UpdateDriverDll";
        _uninstall.Name = "UninstallDriverPlugin";
        _actions = UiTheme.Toolbar(upload, _details, _update, _uninstall, refresh, open, close);
        _grid.SelectionChanged += (_, _) => UpdateActions();
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await ShowDetailsAsync(); };
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12), BackColor = Color.White };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status.Dock = DockStyle.Fill;
        layout.Controls.Add(_actions, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.SizeChanged += (_, _) => _status.MaximumSize = new Size(Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal), 0);
        Controls.Add(layout);
        Shown += async (_, _) => await RefreshGridAsync();
        UpdateActions();
    }

    private DriverListItem? SelectedDriver => _grid.CurrentRow?.DataBoundItem is DriverListItem row
        && _drivers.TryGetValue(row.Id, out var driver) ? driver : null;

    private void UpdateActions()
    {
        var selected = SelectedDriver;
        _details.Enabled = !_busy && selected is not null;
        _update.Enabled = _uninstall.Enabled = !_busy && selected is not null;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _actions.Enabled = _grid.Enabled = !busy;
        UpdateActions();
    }

    private async Task InstallAsync(bool updating)
    {
        if (_busy) return;
        var selected = SelectedDriver;
        if (updating && selected is null) return;
        var related = updating ? await ReadDetailsAsync(selected!.Id) : null;
        if (updating && related is null) return;
        using var picker = new OpenFileDialog
        {
            Title = updating ? $"更新驱动：{selected!.Name}" : "选择已编译的 Edge Gateway 驱动 DLL",
            Filter = "驱动程序集 (*.dll)|*.dll", CheckFileExists = true, Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var explanation = updating
            ? $"将更新 {selected!.Name} ({selected.Id})，当前版本 {selected.Version}。新 DLL 须保持相同驱动标识和程序集名称，并提高版本号。请先停止使用这些驱动的设备。受影响驱动：{string.Join("、", related!.RelatedDriverIds)}。"
            : "将新增并加载所选驱动。";
        if (MessageBox.Show(this, explanation + Environment.NewLine +
            "DLL 在网关进程内执行，请确认来源可信。是否继续？", updating ? "确认更新驱动" : "确认安装驱动",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunMutationAsync(() => updating
            ? _application.UpdateDriverPluginAsync(selected!.Id, picker.FileName)
            : _application.InstallDriverPluginAsync(picker.FileName));
    }

    private async Task UninstallAsync()
    {
        if (_busy || SelectedDriver is not { } selected) return;
        var related = await ReadDetailsAsync(selected.Id);
        if (related is null) return;
        if (MessageBox.Show(this, $"删除 {selected.Name} ({selected.Id})，版本 {selected.Version}？" + Environment.NewLine +
            "同 DLL 受影响驱动：" + string.Join("、", related.RelatedDriverIds) + Environment.NewLine +
            "任何设备仍引用这些驱动时不能删除，请先删除设备或更换驱动。删除后重启也不会重新加载。",
            "确认删除驱动", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunMutationAsync(() => _application.UninstallDriverPluginAsync(selected.Id));
    }

    private async Task RunMutationAsync(Func<Task<string>> action)
    {
        SetBusy(true);
        _status.Text = "正在处理驱动插件，请稍候……";
        try
        {
            var message = await action();
            await RefreshGridAsync();
            _status.Text = message + Environment.NewLine + _status.Text;
        }
        catch (Exception ex)
        {
            _status.Text = "操作失败：" + ex.GetBaseException().Message;
            ShowMessage(_status.Text, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task<DriverDetails?> ReadDetailsAsync(string id)
    {
        SetBusy(true);
        try { return await _application.GetDriverDetailsAsync(id); }
        catch (Exception ex) { ShowMessage(ex.Message, MessageBoxIcon.Error); return null; }
        finally { SetBusy(false); }
    }

    private async Task ShowDetailsAsync()
    {
        if (_busy || SelectedDriver is not { } selected) return;
        SetBusy(true);
        DriverDetails details;
        try { details = await _application.GetDriverDetailsAsync(selected.Id); }
        catch (Exception ex) { ShowMessage(ex.Message, MessageBoxIcon.Error); return; }
        finally { SetBusy(false); }
        using var dialog = new DriverDetailsDialog(details);
        dialog.ShowDialog(this);
    }

    private async Task RefreshGridAsync()
    {
        if (_refreshing || IsDisposed) return;
        _refreshing = true;
        _actions.Enabled = _grid.Enabled = false;
        try
        {
            var drivers = await _application.GetDriverListAsync();
            if (IsDisposed) return;
            _drivers.Clear();
            foreach (var driver in drivers) _drivers[driver.Id] = driver;
            var rows = drivers.Select(driver => driver with
            {
                Source = driver.Source switch { "bundled" => "随程序提供", "uploaded" => "上传安装", _ => "已注册" },
                Version = driver.Version is "" or "unknown" ? "未知" : driver.Version,
                AssemblyName = driver.AssemblyName.Length == 0 ? "未知" : driver.AssemblyName,
                Sha256 = driver.Sha256.Length == 0 ? "未记录" : driver.Sha256
            }).ToArray();
            GridViewState.Rebind(_grid, rows, row => row.Id);
            _status.Text = $"当前可用驱动 {drivers.Count(driver => driver.IsAvailable)} 个，上传安装 {drivers.Count(driver => driver.Source == "uploaded")} 个，未加载 {drivers.Count(driver => !driver.IsAvailable)} 个。{Environment.NewLine}内置与上传驱动均可更新或删除；同 DLL 的驱动一并处理。{Environment.NewLine}插件目录：{_pluginDirectory}";
            UpdateActions();
        }
        catch (Exception exception)
        {
            if (!IsDisposed) _status.Text = "读取驱动列表失败：" + exception.Message;
        }
        finally
        {
            _refreshing = false;
            if (!IsDisposed && !_busy) SetBusy(false);
        }
    }

    private void ShowMessage(string message, MessageBoxIcon icon) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, icon);
}
