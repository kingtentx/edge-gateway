using EdgeGateway.Model;
using EdgeGateway.Application;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly ComboBox _deviceGroupFilter = new() { Name = "DeviceGroupFilter", DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    private readonly ComboBox _deviceChannelFilter = new() { Name = "DeviceChannelFilter", DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly TextBox _deviceSearch = new() { Name = "DeviceSearch", Width = 200, PlaceholderText = "设备 / 驱动 / 点位 / 地址" };
    private bool _bindingDeviceFilters, _rebindingDevices;

    private FlowLayoutPanel BuildDeviceFilters()
    {
        EditorLayout.ConfigureDropDown(_deviceChannelFilter, 500, 320);
        EditorLayout.ConfigureDropDown(_deviceGroupFilter, 400, 320);
        _deviceChannelFilter.SelectedIndexChanged += (_, _) => { if (!_bindingDeviceFilters) BindDeviceRows(); };
        _deviceGroupFilter.SelectedIndexChanged += (_, _) => { if (!_bindingDeviceFilters) BindDeviceRows(); };
        _deviceSearch.TextChanged += (_, _) => { if (!_bindingDeviceFilters) BindDeviceRows(); };
        var clear = UiTheme.Button("清除筛选", (_, _) =>
        {
            _bindingDeviceFilters = true;
            _deviceChannelFilter.SelectedIndex = _deviceGroupFilter.SelectedIndex = 0;
            _deviceSearch.Clear();
            _bindingDeviceFilters = false;
            BindDeviceRows();
        });
        clear.Margin = new Padding(0, 23, 0, 6);
        var toolbar = UiTheme.Toolbar(
            UiTheme.FilterField("通信通道", _deviceChannelFilter),
            UiTheme.FilterField("设备分组", _deviceGroupFilter),
            UiTheme.FilterField("查找设备", _deviceSearch), clear);
        toolbar.Name = "DeviceFilters";
        toolbar.Padding = new Padding(0, 0, 0, 6);
        return toolbar;
    }

    private void RefreshDeviceFilters()
    {
        var channel = _deviceChannelFilter.SelectedItem as ChannelChoice;
        var group = (_deviceGroupFilter.SelectedItem as GroupChoice)?.Group;
        _bindingDeviceFilters = true;
        try
        {
            _deviceChannelFilter.Items.Clear();
            _deviceChannelFilter.Items.Add(new ChannelChoice(null, "全部通道"));
            _deviceChannelFilter.Items.Add(new ChannelChoice(null, "独立通信", true));
            _deviceChannelFilter.Items.AddRange(_configuration.Channels.Select(c => new ChannelChoice(c.Id, c.Name)).Cast<object>().ToArray());
            _deviceChannelFilter.SelectedItem = _deviceChannelFilter.Items.Cast<ChannelChoice>()
                .FirstOrDefault(c => c.Id == channel?.Id && c.IndependentOnly == (channel?.IndependentOnly ?? false)) ?? _deviceChannelFilter.Items[0];
            _deviceGroupFilter.Items.Clear();
            _deviceGroupFilter.Items.Add(new GroupChoice(null, "全部分组"));
            _deviceGroupFilter.Items.AddRange(_configuration.Devices.Select(d => d.Group).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).Select(g => new GroupChoice(g, string.IsNullOrEmpty(g) ? "未分组" : g)).Cast<object>().ToArray());
            _deviceGroupFilter.SelectedItem = _deviceGroupFilter.Items.Cast<GroupChoice>()
                .FirstOrDefault(g => string.Equals(g.Group, group, StringComparison.OrdinalIgnoreCase)) ?? _deviceGroupFilter.Items[0];
        }
        finally { _bindingDeviceFilters = false; }
    }

    private void BindDeviceRows(Guid? selectDevice = null)
    {
        var selected = selectDevice ?? SelectedDevice()?.Id;
        var channel = _deviceChannelFilter.SelectedItem as ChannelChoice;
        var group = (_deviceGroupFilter.SelectedItem as GroupChoice)?.Group;
        var devices = DeviceConfigurationOperations.Filter(_configuration.Devices, group, _deviceSearch.Text, channel?.Id, channel?.IndependentOnly ?? false);
        if (selectDevice.HasValue && devices.All(d => d.Id != selectDevice))
        {
            _bindingDeviceFilters = true;
            _deviceGroupFilter.SelectedIndex = _deviceChannelFilter.SelectedIndex = 0;
            _deviceSearch.Clear();
            _bindingDeviceFilters = false;
            devices = _configuration.Devices;
        }
        _rebindingDevices = true;
        try
        {
            var rows = devices.Select(d => new DeviceRow(d.Id, d.Name,
                _configuration.Channels.FirstOrDefault(c => c.Id == d.ChannelId)?.Name ?? "独立通信",
                d.Group, d.DriverId, d.Enabled ? "是" : "否", "已停止", d.Tags.Count, d.PollIntervalMs)).ToList();
            GridViewState.Rebind(_deviceGrid, rows, row => row.Id, selected);
            _deviceSummary.Text = $"{rows.Count} / {_configuration.Devices.Count} 台设备";
        }
        finally { _rebindingDevices = false; }
        BindTags();
        UpdateDeviceConfigurationCommands();
    }

    private Task CopyDeviceAsync(Guid? deviceId = null)
    {
        if (!CanEdit()) return Task.CompletedTask;
        var original = deviceId.HasValue ? _configuration.Devices.FirstOrDefault(d => d.Id == deviceId) : SelectedDevice();
        if (original is null) { SetFooter("请先选择需要复制的设备。"); return Task.CompletedTask; }
        var draft = DeviceConfigurationOperations.Copy(original, _configuration.Devices.Select(d => d.Name));
        using var editor = new DeviceConfigurationDialog(draft, _drivers, channels: _configuration.Channels);
        editor.Text = "复制设备 · " + original.Name;
        BindEditorApply(editor, null, async () =>
        {
            var copy = editor.Value ?? throw new InvalidOperationException("设备副本尚未通过检查。");
            var applied = await _application.AddDeviceAsync(copy);
            CompleteConfigurationCommit(applied, copy.Id, "设备和点位已复制并自动保存；副本尚未启动，请核对连接参数。");
        });
        editor.ShowDialog(this);
        return Task.CompletedTask;
    }

    private Task EditChannelAsync(bool add, Guid? selectedChannelId = null)
    {
        if (!CanEdit()) return Task.CompletedTask;
        if (_application.IsRunning) { SetFooter("请停止全部设备后维护通信通道；设备与点位仍可独立编辑。"); return Task.CompletedTask; }
        var channelId = selectedChannelId ?? (_deviceChannelFilter.SelectedItem as ChannelChoice)?.Id;
        var channel = add ? new ChannelConfiguration { Name = "通道 " + (_configuration.Channels.Count + 1) }
            : _configuration.Channels.FirstOrDefault(c => c.Id == channelId);
        if (channel is null) { SetFooter("请在通道筛选中选择要编辑的具体通道。"); return Task.CompletedTask; }
        using var editor = new ChannelEditor(Clone(channel));
        BindEditorApply(editor, null, async () =>
        {
            var applied = await _application.UpsertChannelAsync(editor.Value, add);
            CompleteConfigurationCommit(applied, successMessage: "通信通道已自动保存。");
            _deviceChannelFilter.SelectedItem = _deviceChannelFilter.Items.Cast<ChannelChoice>().First(c => c.Id == channel.Id);
        });
        editor.ShowDialog(this);
        return Task.CompletedTask;
    }

    private async Task DeleteChannelAsync(Guid? selectedChannelId = null)
    {
        if (!CanEdit()) return;
        if (_application.IsRunning) { SetFooter("请停止全部设备后删除通道。"); return; }
        var id = selectedChannelId ?? (_deviceChannelFilter.SelectedItem as ChannelChoice)?.Id;
        var channel = _configuration.Channels.FirstOrDefault(c => c.Id == id);
        if (channel is null) { SetFooter("请先选择具体通道。"); return; }
        if (_configuration.Devices.Any(d => d.ChannelId == id)) { SetFooter("该通道仍有设备，请先在设备配置中迁移到其他通道或独立通信。"); return; }
        if (MessageBox.Show(this, $"删除通信通道“{channel.Name}”？", "删除通道", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunBusyAsync(async () => CompleteConfigurationCommit(await _application.DeleteChannelAsync(channel.Id),
            successMessage: "通信通道已删除并自动保存。"));
    }

    private async Task ProbeSelectionAsync(bool selectedPoint)
    {
        var device = SelectedDevice();
        if (device is null) { SetFooter("请先选择设备。"); return; }
        var tag = selectedPoint ? SelectedTag() : null;
        if (selectedPoint && tag is null) { SetFooter("请先选择需要读取的点位。"); return; }
        var result = await _application.ProbeDeviceAsync(device.Id, tag?.Id);
        var state = result.State switch
        {
            DeviceProbeState.Succeeded => "单次读取成功", DeviceProbeState.ConnectedOnly => "仅连接成功（未读点）",
            DeviceProbeState.TimedOut => "诊断超时", DeviceProbeState.Cancelled => "诊断已取消", _ => "诊断失败"
        };
        var sample = result.Sample;
        var details = $"设备：{device.Name}\n驱动：{result.DriverId}\n结果：{state}\n连接：{(result.Connected ? "已建立" : "未建立")}\n会话：{(result.UsedRunningSession ? "复用运行会话" : "临时连接，完成后关闭")}\n耗时：{result.Elapsed.TotalMilliseconds:F1} ms\n操作编号：{result.OperationId}";
        if (sample is not null) details += $"\n\n点位：{sample.TagName}\n地址：{sample.Address}\n原始值：{ValueText(sample.RawValue)}\n工程值：{ValueText(sample.Value)} {sample.Unit}\n质量：{sample.Quality}\n采样时间：{sample.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}";
        if (!string.IsNullOrEmpty(result.Error)) details += "\n\n诊断说明：" + result.Error;
        SetFooter(state + " · " + device.Name);
        MessageBox.Show(this, details, "连接与读点诊断", MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private sealed record ChannelChoice(Guid? Id, string Name, bool IndependentOnly = false)
    { public override string ToString() => Name; }
    private sealed record GroupChoice(string? Group, string Name)
    { public override string ToString() => Name; }
}

internal sealed class ChannelEditor : EditorDialog
{
    public ChannelConfiguration Value { get; }
    private readonly TextBox _name, _description;
    private readonly NumericUpDown _delay;
    public ChannelEditor(ChannelConfiguration value) : base("通信通道", "同一通道的采集、诊断和写入依次执行；不同通道并行。设备连接参数仍在设备配置中设置。")
    {
        Value = value;
        var fields = AddTab("通道设置");
        Section(fields, "通道信息", "设置名称与通信间隔。");
        _name = TextInput(value.Name); _name.Name = "ChannelName";
        _delay = NumberInput(value.InterRequestDelayMs, 0, 60000); _delay.Name = "ChannelDelay";
        _description = TextInput(value.Description); _description.Name = "ChannelDescription";
        Field(fields, "通道名称", _name);
        Field(fields, "请求间隔 (ms)", _delay);
        Field(fields, "说明", _description);
        Hint(fields, "间隔从上一批通信完成开始计算。该设置控制调度，不合并驱动连接；同一物理串口仍只允许一个启用设备。");
    }
    protected override void Commit()
    {
        Value.Name = Required(_name, "通道名称");
        Value.InterRequestDelayMs = (int)_delay.Value;
        Value.Description = _description.Text.Trim();
    }
}
