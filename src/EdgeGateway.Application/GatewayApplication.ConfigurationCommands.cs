using EdgeGateway.Model;

namespace EdgeGateway.Application;

public sealed partial class GatewayApplication
{
    public Task<ConfigurationApplyResult> AddDeviceAsync(DeviceConfiguration device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var draft = GatewayJson.Clone(device);
        return ChangeAsync(candidate =>
        {
            if (candidate.Devices.Any(existing => existing.Id == draft.Id)) throw new InvalidOperationException("设备标识已存在，不能重复添加。");
            candidate.Devices.Add(draft);
        }, cancellationToken);
    }

    public Task<ConfigurationApplyResult> UpdateDeviceAsync(DeviceConfiguration device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var draft = GatewayJson.Clone(device);
        return ChangeAsync(candidate =>
        {
            var previous = FindDevice(candidate, draft.Id);
            candidate.Devices[candidate.Devices.IndexOf(previous)] = draft;
        }, cancellationToken);
    }

    public Task<ConfigurationApplyResult> DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        ChangeAsync(candidate => candidate.Devices.Remove(FindDevice(candidate, deviceId)), cancellationToken);

    public Task<ConfigurationApplyResult> AddTagAsync(Guid deviceId, TagConfiguration tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var draft = GatewayJson.Clone(tag);
        return ChangeAsync(candidate =>
        {
            var device = FindDevice(candidate, deviceId);
            if (device.Tags.Any(existing => existing.Id == draft.Id)) throw new InvalidOperationException("点位标识已存在，不能重复添加。");
            device.Tags.Add(draft);
        }, cancellationToken);
    }

    public Task<ConfigurationApplyResult> UpdateTagAsync(Guid deviceId, TagConfiguration tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var draft = GatewayJson.Clone(tag);
        return ChangeAsync(candidate =>
        {
            var device = FindDevice(candidate, deviceId);
            var index = device.Tags.FindIndex(existing => existing.Id == draft.Id);
            if (index < 0) throw new InvalidOperationException("点位不存在。");
            device.Tags[index] = draft;
        }, cancellationToken);
    }

    public Task<ConfigurationApplyResult> DeleteTagAsync(Guid deviceId, Guid tagId, CancellationToken cancellationToken = default) =>
        ChangeAsync(candidate =>
        {
            var device = FindDevice(candidate, deviceId);
            var tag = device.Tags.FirstOrDefault(existing => existing.Id == tagId) ?? throw new InvalidOperationException("点位不存在。");
            device.Tags.Remove(tag);
        }, cancellationToken);

    public Task<ConfigurationApplyResult> AppendDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var drafts = GatewayJson.Clone(devices);
        return ChangeAsync(candidate => candidate.Devices.AddRange(drafts), cancellationToken);
    }

    public Task<ConfigurationApplyResult> ReplaceDeviceTagsAsync(Guid deviceId, IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var drafts = GatewayJson.Clone(tags);
        return ChangeAsync(candidate => FindDevice(candidate, deviceId).Tags = drafts.ToList(), cancellationToken);
    }

    public Task<ConfigurationApplyResult> SaveDeviceGroupAsync(string? originalName, string name, CancellationToken cancellationToken = default)
    {
        var next = name?.Trim() ?? "";
        if (next.Length is 0 or > 80) throw new ArgumentException("分组名称须为 1–80 个字符。", nameof(name));
        return ChangeAsync(candidate =>
        {
            var index = originalName is null ? -1 : candidate.DeviceGroups.FindIndex(group => string.Equals(group, originalName, StringComparison.OrdinalIgnoreCase));
            if (originalName is not null && index < 0) throw new InvalidOperationException("分组不存在，请刷新列表。");
            if (candidate.DeviceGroups.Where((_, i) => i != index).Any(group => string.Equals(group, next, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("分组名称已存在。");
            if (index < 0) candidate.DeviceGroups.Add(next);
            else
            {
                candidate.DeviceGroups[index] = next;
                foreach (var device in candidate.Devices.Where(device => string.Equals(device.Group, originalName, StringComparison.OrdinalIgnoreCase)))
                    device.Group = next;
            }
        }, cancellationToken, requireStopped: true);
    }

    public Task<ConfigurationApplyResult> DeleteDeviceGroupAsync(string name, CancellationToken cancellationToken = default) =>
        ChangeAsync(candidate =>
        {
            if (candidate.Devices.Any(device => string.Equals(device.Group, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("该分组仍有设备，请先修改设备分组后再删除。");
            var index = candidate.DeviceGroups.FindIndex(group => string.Equals(group, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidOperationException("分组不存在，请刷新列表。");
            candidate.DeviceGroups.RemoveAt(index);
        }, cancellationToken, requireStopped: true);

    private static void IncludeDeviceGroups(GatewayConfiguration candidate)
    {
        foreach (var device in candidate.Devices)
            if (!string.IsNullOrWhiteSpace(device.Group) && !candidate.DeviceGroups.Contains(device.Group, StringComparer.OrdinalIgnoreCase))
                candidate.DeviceGroups.Add(device.Group);
    }

    public Task<ConfigurationApplyResult> UpsertChannelAsync(ChannelConfiguration channel, bool isNew, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var draft = GatewayJson.Clone(channel);
        return ChangeAsync(candidate =>
        {
            var index = candidate.Channels.FindIndex(existing => existing.Id == draft.Id);
            if (isNew && index >= 0) throw new InvalidOperationException("通道标识已存在，不能重复添加。");
            if (!isNew && index < 0) throw new InvalidOperationException("通道不存在。");
            if (isNew) candidate.Channels.Add(draft);
            else candidate.Channels[index] = draft;
        }, cancellationToken, requireStopped: true);
    }

    public Task<ConfigurationApplyResult> DeleteChannelAsync(Guid channelId, CancellationToken cancellationToken = default) =>
        ChangeAsync(candidate =>
        {
            var channel = candidate.Channels.FirstOrDefault(existing => existing.Id == channelId)
                ?? throw new InvalidOperationException("通道不存在。");
            if (candidate.Devices.Any(device => device.ChannelId == channelId))
                throw new InvalidOperationException("该通道仍有设备，请先迁移设备到其他通道或独立通信。");
            candidate.Channels.Remove(channel);
        }, cancellationToken, requireStopped: true);

    public Task<ConfigurationApplyResult> ApplyGlobalSettingsAsync(GatewayConfiguration draft,
        ConfigurationApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        var settings = Capture(draft);
        return ChangeAsync(candidate =>
        {
            candidate.GatewayId = settings.GatewayId;
            candidate.Name = settings.Name;
            candidate.AutoStart = settings.AutoStart;
            candidate.MaxOutboxMessages = settings.MaxOutboxMessages;
            candidate.PublishIntervalMs = settings.PublishIntervalMs;
            candidate.ReportMode = settings.ReportMode;
            candidate.HeartbeatIntervalMs = settings.HeartbeatIntervalMs;
            candidate.Mqtt = settings.Mqtt;
        }, cancellationToken, options, requireStopped: true);
    }

    private async Task<ConfigurationApplyResult> ChangeAsync(Action<GatewayConfiguration> change, CancellationToken cancellationToken,
        ConfigurationApplyOptions? options = null, bool requireStopped = false)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (requireStopped && _runtime.IsRunning) throw new InvalidOperationException("请先停止全部设备，再应用全局、分组或通道设置。");
            // Build the candidate only after admission. Concurrent commands for different
            // devices therefore retain changes committed while this command was waiting.
            var candidate = GatewayJson.Clone(_configuration);
            IncludeDeviceGroups(candidate);
            change(candidate);
            IncludeDeviceGroups(candidate);
            return await ApplyCoreAsync(candidate, options, cancellationToken).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }
}
