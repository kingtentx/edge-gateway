namespace EdgeGateway.Model;

public sealed record ServerMemoryUsage(long TotalBytes, long AvailableBytes)
{
    public double UsedPercent => 100d * (TotalBytes - AvailableBytes) / TotalBytes;
}

public sealed record ServerDiskUsage(string Name, long? TotalBytes, long? FreeBytes, string? Error = null)
{
    public double? UsedPercent => TotalBytes is > 0 && FreeBytes is >= 0
        ? 100d * (TotalBytes.Value - FreeBytes.Value) / TotalBytes.Value : null;
}

public sealed record ServerResourceSnapshot(DateTimeOffset Timestamp, string MachineName, int LogicalProcessors,
    double? CpuUsagePercent, ServerMemoryUsage? Memory, IReadOnlyList<ServerDiskUsage> Disks, IReadOnlyList<string> Errors)
{
    public IReadOnlyList<ServerResourceReading> History { get; init; } = [];
}

public sealed record ServerResourceReading(DateTimeOffset Timestamp, double? CpuUsagePercent, double? MemoryUsagePercent);

public sealed record GatewayOverviewSummary(int TotalDevices, int EnabledDevices, int OnlineDevices,
    int AbnormalDevices, int StoppedDevices, int ConnectingDevices, int ReconnectingDevices, int FaultedDevices,
    int EnabledPoints, int GoodPoints, int BadPoints, int UncertainPoints, int WaitingPoints)
{
    public double? OnlinePercent => EnabledDevices == 0 ? null : 100d * OnlineDevices / EnabledDevices;
}
