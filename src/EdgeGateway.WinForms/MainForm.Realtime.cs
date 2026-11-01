using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly RealtimeDisplayState _realtimeDisplay = new();
    private readonly Dictionary<Guid, AntdUI.TreeItem> _realtimeDeviceNodes = [];
    private Guid? _realtimeDeviceId;
    private string? _realtimeSelectedGroup;
    private bool _bindingRealtimeTree;
    private sealed record RealtimeGroupChoice(string? Group, string Text) { public override string ToString() => Text; }
    private sealed record RealtimeScope(Guid? DeviceId, string? Group);

    private void BindRealtimeBrowserEvents()
    {
        _realtimePageView.GroupSelector.SelectedIndexChanged += (_, _) =>
        {
            if (_bindingRealtimeTree) return;
            _realtimeDeviceId = null; _realtimeSelectedGroup = null;
            BindRealtimeTree();
            RefreshRealtime(_application.GetLatest());
        };
        _realtimePageView.DeviceTree.SelectChanged += (_, e) =>
        {
            if (_bindingRealtimeTree || e.Item.Tag is not RealtimeScope scope) return;
            _realtimeDeviceId = scope.DeviceId; _realtimeSelectedGroup = scope.Group;
            RefreshRealtime(_application.GetLatest());
        };
    }

    private void BindRealtimeGroups()
    {
        var selected = (_realtimePageView.GroupSelector.SelectedItem as RealtimeGroupChoice)?.Group;
        _bindingRealtimeTree = true;
        try
        {
            var groups = _configuration.DeviceGroups.Concat(_configuration.Devices.Select(device => device.Group))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(group => group)
                .Select(group => new RealtimeGroupChoice(group, string.IsNullOrWhiteSpace(group) ? "未分组" : group));
            var choices = new[] { new RealtimeGroupChoice(null, "全部分组") }.Concat(groups).ToArray();
            _realtimePageView.GroupSelector.DataSource = choices;
            _realtimePageView.GroupSelector.SelectedIndex = Math.Max(0,
                Array.FindIndex(choices, choice => string.Equals(choice.Group, selected, StringComparison.OrdinalIgnoreCase)));
        }
        finally { _bindingRealtimeTree = false; }
        BindRealtimeTree();
    }

    private void BindRealtimeTree()
    {
        var tree = _realtimePageView.DeviceTree;
        var filter = (_realtimePageView.GroupSelector.SelectedItem as RealtimeGroupChoice)?.Group;
        var devices = _configuration.Devices.Where(device => filter is null
            || string.Equals(device.Group, filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var collapsed = tree.Items.SelectMany(root => root.Sub).Where(item => !item.Expand)
            .Select(item => item.ID).ToHashSet();
        _bindingRealtimeTree = true;
        try
        {
            tree.Items.Clear(); _realtimeDeviceNodes.Clear();
            var root = new AntdUI.TreeItem(filter is null ? "全部设备" : string.IsNullOrWhiteSpace(filter) ? "未分组" : filter)
                { ID = "all", Expand = true, Tag = new RealtimeScope(null, filter) };
            AntdUI.TreeItem selection = root;
            foreach (var grouping in devices.GroupBy(device => device.Group, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key))
            {
                var groupId = "group:" + grouping.Key;
                var node = new AntdUI.TreeItem(string.IsNullOrWhiteSpace(grouping.Key) ? "未分组" : grouping.Key)
                    { ID = groupId, Expand = !collapsed.Contains(groupId), Tag = new RealtimeScope(null, grouping.Key) };
                if (!_realtimeDeviceId.HasValue && string.Equals(grouping.Key, _realtimeSelectedGroup, StringComparison.OrdinalIgnoreCase)) selection = node;
                foreach (var device in grouping.OrderBy(device => device.Name))
                {
                    var leaf = new AntdUI.TreeItem(device.Name)
                        { ID = device.Id.ToString("D"), IconSvg = "PauseCircleOutlined", Tag = new RealtimeScope(device.Id, device.Group) };
                    node.Sub.Add(leaf); _realtimeDeviceNodes.Add(device.Id, leaf);
                    if (device.Id == _realtimeDeviceId) { selection = leaf; node.Expand = true; }
                }
                root.Sub.Add(node);
            }
            tree.Items.Add(root);
            var scope = (RealtimeScope)selection.Tag!;
            _realtimeDeviceId = scope.DeviceId; _realtimeSelectedGroup = scope.Group;
            tree.Select(selection, false);
        }
        finally { _bindingRealtimeTree = false; }
    }

    private void SelectRealtimeDevice(Guid? id)
    {
        if (id.HasValue && !_realtimeDeviceNodes.ContainsKey(id.Value))
            _realtimePageView.GroupSelector.SelectedIndex = 0;
        var tree = _realtimePageView.DeviceTree;
        var node = id.HasValue ? _realtimeDeviceNodes.GetValueOrDefault(id.Value) : tree.Items.FirstOrDefault();
        if (node is null) return;
        if (id.HasValue)
            foreach (var group in tree.Items.SelectMany(root => root.Sub))
                if (group.Sub.Contains(node)) group.Expand = true;
        tree.Select(node, false);
        if (node.Tag is RealtimeScope scope) { _realtimeDeviceId = scope.DeviceId; _realtimeSelectedGroup = scope.Group; }
    }

    private void RefreshRealtime(IReadOnlyList<TelemetrySample> samples, IReadOnlyList<DeviceRuntimeStatus>? statuses = null)
    {
        if (_writeButton is null) return;
        var states = statuses ?? _application.GetDeviceStatuses();
        var stateById = states.ToDictionary(state => state.DeviceId);
        foreach (var device in _configuration.Devices)
        {
            if (!_realtimeDeviceNodes.TryGetValue(device.Id, out var node)) continue;
            var state = stateById.TryGetValue(device.Id, out var status) ? status.State : DeviceState.Stopped;
            var icon = !device.Enabled ? "MinusCircleOutlined" : state switch
            {
                DeviceState.Running => "PlayCircleFilled",
                DeviceState.Connecting or DeviceState.Reconnecting => "SyncOutlined",
                DeviceState.Faulted => "ExclamationCircleFilled",
                _ => "PauseCircleOutlined"
            };
            if (node.Text != device.Name) node.Text = device.Name;
            if (node.IconSvg != icon) node.IconSvg = icon;
        }
        var search = _realtimeSearch.Text.Trim();
        var devices = _configuration.Devices.ToDictionary(device => device.Id);
        var access = _configuration.Devices.SelectMany(device => device.Tags.Select(tag =>
            (Key: (device.Id, tag.Id), Text: tag.Writable ? "读 / 写" : "只读"))).ToDictionary(item => item.Key, item => item.Text);
        var rows = _realtimeDisplay.Update(_configuration.Devices, states, samples)
            .Where(point => (!_realtimeDeviceId.HasValue || point.Sample.DeviceId == _realtimeDeviceId)
                && (_realtimeSelectedGroup is null || string.Equals(devices[point.Sample.DeviceId].Group, _realtimeSelectedGroup, StringComparison.OrdinalIgnoreCase))
                && (search.Length == 0 || point.Sample.TagName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || point.Sample.Address.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(point => point.Sample.DeviceName).ThenBy(point => point.Sample.TagName)
            .Select(point => ToRow(point.Sample) with
            {
                Access = access[(point.Sample.DeviceId, point.Sample.TagId)],
                Time = point.HasSample ? point.Sample.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss.fff") : "—",
                Value = point.HasSample ? ValueText(point.Sample.Value) : "—",
                RawValue = point.HasSample ? ValueText(point.Sample.RawValue) : "—"
            }).ToList();
        _realtimePageView.UpdatePointSummary(rows.Select(row => row.Sample.DeviceId).Distinct().Count(), rows.Count);
        GridViewState.UpdateRows(_realtimeGrid, rows, row => (row.Sample.DeviceId, row.Sample.TagId));
        UpdateWriteButton();
    }
}
