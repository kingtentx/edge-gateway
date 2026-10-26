using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Application;

public sealed partial class GatewayApplication
{
    private IConfigurationWorkbookFiles Workbooks => _workbooks ?? throw new InvalidOperationException("宿主未提供 Excel 文件服务。");

    public Task<DeviceConfigurationWorkbook> ReadDeviceWorkbookAsync(string path, CancellationToken cancellationToken = default) =>
        Workbooks.ReadDevicesAsync(path, cancellationToken);

    public DeviceWorkbookPreview PreviewDeviceWorkbook(DeviceConfigurationWorkbook workbook, bool updateExisting)
    {
        var candidate = GetConfiguration();
        var existing = candidate.Devices.Select(device => device.Id).ToHashSet();
        var channels = candidate.Channels.Count;
        try
        {
            MergeDeviceWorkbook(candidate, workbook, updateExisting);
            var changes = workbook.Devices.Select(device => new DeviceWorkbookChange(device.Id, device.Name, device.DriverId,
                candidate.Devices.Single(item => item.Id == device.Id).Tags.Count, existing.Contains(device.Id) ? "更新" : "新增")).ToArray();
            var errors = ValidateConfiguration(candidate).ToList();
            if (IsRunning && candidate.Channels.Count > channels) errors.Add("本次导入包含新通道，请先停止全部设备再导入。");
            return new(changes, candidate.Channels.Count - channels, errors);
        }
        catch (InvalidDataException exception) { return new([], 0, [exception.Message]); }
    }

    public Task<ConfigurationApplyResult> ImportDeviceWorkbookAsync(DeviceConfigurationWorkbook workbook, bool updateExisting,
        CancellationToken cancellationToken = default)
    {
        var captured = GatewayJson.Clone(workbook);
        return ChangeAsync(candidate =>
        {
            if (_runtime.IsRunning && captured.Channels.Any(channel => candidate.Channels.All(item => item.Id != channel.Id)))
                throw new InvalidOperationException("本次导入包含新通道，请先停止全部设备再导入。");
            MergeDeviceWorkbook(candidate, captured, updateExisting);
        }, cancellationToken);
    }

    // Called for both the preview and the latest candidate under the application command lock.
    private static void MergeDeviceWorkbook(GatewayConfiguration candidate, DeviceConfigurationWorkbook workbook, bool updateExisting)
    {
        var imported = GatewayJson.Clone(workbook);
        if (imported.Devices.Count is < 1 or > 500 || imported.Channels.Count > 500)
            throw new InvalidDataException("请选择包含 1–500 台设备、最多 500 个通道的工作簿。");
        if (imported.Devices.GroupBy(item => item.Id).Any(group => group.Count() > 1)
            || imported.Channels.GroupBy(item => item.Id).Any(group => group.Count() > 1)
            || imported.Devices.SelectMany(item => item.Tags).GroupBy(item => item.Id).Any(group => group.Count() > 1))
            throw new InvalidDataException("工作簿中的设备、通道或点位标识重复。");
        foreach (var channel in imported.Channels)
        {
            var previous = candidate.Channels.FirstOrDefault(item => item.Id == channel.Id);
            if (previous is null) candidate.Channels.Add(channel);
            else if (previous.Name != channel.Name || previous.Description != channel.Description
                || previous.InterRequestDelayMs != channel.InterRequestDelayMs)
                throw new InvalidDataException($"通道 [{channel.Name}] 与已保存的同标识通道参数不同，请在通道管理中修改，或为新通道清空标识并使用新的通道键。");
        }
        foreach (var device in imported.Devices)
        {
            var index = candidate.Devices.FindIndex(item => item.Id == device.Id);
            if (index < 0) { candidate.Devices.Add(device); continue; }
            if (!updateExisting) throw new InvalidDataException($"设备 [{device.Name}] 的标识已存在，请选择按标识更新；新增副本须清空设备及点位标识。");
            var previous = candidate.Devices[index];
            var tags = previous.Tags.ToList();
            foreach (var tag in device.Tags)
            {
                var tagIndex = tags.FindIndex(item => item.Id == tag.Id);
                if (tagIndex < 0) tags.Add(tag); else tags[tagIndex] = tag;
            }
            var properties = new Dictionary<string, string>(previous.Properties);
            foreach (var pair in device.Properties) properties[pair.Key] = pair.Value;
            device.Tags = tags;
            device.Properties = properties;
            candidate.Devices[index] = device;
        }
    }

    public Task ExportDeviceWorkbookAsync(string path, Guid? deviceId = null, bool template = false, CancellationToken cancellationToken = default)
    {
        var current = GetConfiguration();
        var devices = template ? new List<DeviceConfiguration>
        {
            new() { Name = "设备示例（请修改）", DriverId = "modbus-tcp", Enabled = false,
                Tags = [new() { Name = "点位示例（请修改）", Address = "0" }] }
        } : deviceId.HasValue ? [FindDevice(current, deviceId.Value)] : current.Devices;
        var ids = devices.Select(device => device.ChannelId).ToHashSet();
        return Workbooks.WriteDevicesAsync(path, new() { IsTemplate = template, Devices = devices, Channels = current.Channels.Where(channel => ids.Contains(channel.Id)).ToList() }, cancellationToken);
    }

    public Task<ConfigurationApplyResult> ImportTagFileAsync(Guid deviceId, byte[] content, TagCsvImportMode mode,
        CancellationToken cancellationToken = default)
    {
        var captured = content.ToArray();
        return ChangeAsync(candidate =>
        {
            var device = FindDevice(candidate, deviceId);
            var result = TagCsvExchange.Import(captured, device.Tags, mode);
            if (!result.IsValid) throw new InvalidDataException(string.Join("\n", result.Errors));
            device.Tags = result.Tags.ToList();
        }, cancellationToken);
    }

    public async Task<byte[]> ReadTagWorkbookAsync(string path, CancellationToken cancellationToken = default) =>
        TagCsvExchange.Export(await Workbooks.ReadTagsAsync(path, cancellationToken).ConfigureAwait(false));

    public Task ExportTagWorkbookAsync(string path, Guid? deviceId = null, bool template = false, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TagConfiguration> tags = template ? [new() { Name = "点位示例（请修改）", Address = "0" }]
            : FindDevice(GetConfiguration(), deviceId ?? throw new InvalidOperationException("请先选择设备。")).Tags;
        return Workbooks.WriteTagsAsync(path, tags, cancellationToken);
    }
}
