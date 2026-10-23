namespace EdgeGateway.Model;

/// <summary>Communication scheduling domain. Connections remain owned by each driver.</summary>
public sealed class ChannelConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新通道";
    public int InterRequestDelayMs { get; set; }
    public string Description { get; set; } = "";
}

public enum DeviceScanMode { DeviceRate, TagRate }
public enum TelemetryReportMode { Periodic, OnChange, OnChangeWithHeartbeat }

public sealed record DeviceCommunicationStatistics(Guid DeviceId, long ReadRequests, long WriteRequests,
    long FailedRequests, double LastDurationMs, DateTimeOffset? LastRequestAt, string? LastError,
    DateTimeOffset? DemotedUntil);
