using EdgeGateway.Application;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

/// <summary>Local administrator UI for installing trusted, single-file driver factory plugins.</summary>
internal sealed partial class DriverPluginDialog : DpiAwareForm
{
    private readonly IGatewayApplication _application;
    private readonly string _pluginDirectory;
    private bool _refreshing;
    private bool _busy;

    public DriverPluginDialog() { _application = null!; _pluginDirectory = ""; InitializeComponent(); }

    public DriverPluginDialog(IGatewayApplication application, string driverDirectory)
    {
        _application = application;
        _pluginDirectory = Path.GetFullPath(driverDirectory);
        Text = "驱动插件管理";
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.White;
        ConfigureWindow(new Size(980, 650), new Size(700, 480));
        StartPosition = FormStartPosition.CenterParent;

        InitializeComponent();
        upload.Click += async (_, _) => await InstallAsync(false);
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(_pluginDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_pluginDirectory) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowMessage(ex.Message, MessageBoxIcon.Error); }
        };
        refresh.Click += async (_, _) => await RefreshGridAsync();
        close.Click += (_, _) => Close();
        _details.Click += async (_, _) => await ShowDetailsAsync();
        _update.Click += async (_, _) => await InstallAsync(true);
        _uninstall.Click += async (_, _) => await UninstallAsync();
        _grid.SelectionChanged += (_, _) => UpdateActions();
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await ShowDetailsAsync(); };
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        layout.SizeChanged += (_, _) => _status.MaximumSize = new Size(Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal), 0);
        Shown += async (_, _) => await RefreshGridAsync();
        UpdateActions();
    }

    private DriverAssemblyRow? SelectedAssembly => _grid.CurrentRow?.DataBoundItem as DriverAssemblyRow;
    private DriverListItem? SelectedDriver => SelectedAssembly is { } row ? row.Members[0] with { Name = row.Name } : null;

    private void ShowFeedback(string message)
    {
        _status.Text = message;
        _status.Visible = !string.IsNullOrWhiteSpace(message);
    }

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
        if (MessageBox.Show(this, $"卸载 {selected.Name} ({selected.Id})，版本 {selected.Version}？" + Environment.NewLine +
            "同 DLL 受影响驱动：" + string.Join("、", related.RelatedDriverIds) + Environment.NewLine +
            "任何设备仍引用这些驱动时不能卸载，请先移除设备引用或更换驱动。卸载会移除注册记录，保留 drivers 中的 DLL，重启后不会自动加载。",
            "确认卸载驱动", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunMutationAsync(() => _application.UninstallDriverPluginAsync(selected.Id));
    }

    private async Task RunMutationAsync(Func<Task<string>> action)
    {
        SetBusy(true);
        ShowFeedback("正在处理驱动插件，请稍候……");
        try
        {
            var message = await action();
            await RefreshGridAsync();
            ShowFeedback(message);
        }
        catch (Exception ex)
        {
            ShowFeedback("操作失败：" + ex.GetBaseException().Message);
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
        if (_busy || SelectedAssembly is not { } selected) return;
        SetBusy(true);
        var details = new List<DriverDetails>();
        try
        {
            foreach (var member in selected.Members) details.Add(await _application.GetDriverDetailsAsync(member.Id));
        }
        catch (Exception ex) { ShowMessage(ex.Message, MessageBoxIcon.Error); return; }
        finally { SetBusy(false); }
        using var dialog = new DriverDetailsDialog(selected.Name, details);
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
            var rows = drivers.GroupBy(driver => string.IsNullOrWhiteSpace(driver.AssemblyName)
                    ? "driver:" + driver.Id : "assembly:" + driver.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .Select(group => DriverAssemblyRow.Create(group.Key, group.ToArray())).ToArray();
            GridViewState.Rebind(_grid, rows, row => row.Key);
            ShowFeedback("");
            UpdateActions();
        }
        catch (Exception exception)
        {
            if (!IsDisposed) ShowFeedback("读取驱动列表失败：" + exception.Message);
        }
        finally
        {
            _refreshing = false;
            if (!IsDisposed && !_busy) SetBusy(false);
        }
    }

    private sealed record DriverAssemblyRow(string Key, string Name, string Source, string Status,
        string DriverIds, string Version, string AssemblyName, string Sha256, DriverListItem[] Members)
    {
        public static DriverAssemblyRow Create(string key, DriverListItem[] members)
        {
            members = members.OrderBy(driver => driver.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            var first = members[0];
            var name = first.AssemblyName.Equals("EdgeGateway.Driver.Modbus", StringComparison.OrdinalIgnoreCase)
                ? "Modbus" : members.Length == 1 ? first.Name.Replace("（原版全模式）", "", StringComparison.Ordinal) : first.AssemblyName;
            return new(key, name,
                Join(members.Select(driver => driver.Source switch { "bundled" => "随程序提供", "uploaded" => "上传安装", _ => "已注册" })),
                members.All(driver => driver.IsAvailable) ? "已加载" : members.Any(driver => driver.IsAvailable) ? "部分未加载" : "未加载",
                Join(members.Select(driver => driver.Id)),
                Join(members.Select(driver => driver.Version is "" or "unknown" ? "未知" : driver.Version)),
                first.AssemblyName.Length == 0 ? "未知" : first.AssemblyName,
                Join(members.Select(driver => driver.Sha256.Length == 0 ? "未记录" : driver.Sha256)), members);
        }

        private static string Join(IEnumerable<string> values) => string.Join(" / ", values.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private void ShowMessage(string message, MessageBoxIcon icon) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, icon);
}
