namespace EdgeGateway.Model;

/// <summary>Portable device configuration only; excludes gateway, platform and database credentials.</summary>
public sealed class DeviceConfigurationWorkbook
{
    public List<ChannelConfiguration> Channels { get; set; } = [];
    public List<DeviceConfiguration> Devices { get; set; } = [];
}

public sealed record DeviceWorkbookChange(Guid Id, string Name, string DriverId, int TagCount, string Change);
public sealed record DeviceWorkbookPreview(IReadOnlyList<DeviceWorkbookChange> Devices, int NewChannels, IReadOnlyList<string> Errors);
