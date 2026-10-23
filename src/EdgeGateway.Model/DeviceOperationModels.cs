namespace EdgeGateway.Model;

public enum DeviceProbeState { Succeeded, ConnectedOnly, Failed, TimedOut, Cancelled }
public enum WriteCommandState { Submitted, Matched, Mismatched, TimedOut, Failed, Cancelled }

public sealed record DeviceProbeOptions
{
    /// <summary>Refuse stopped targets instead of opening a temporary connection (used by RPC).</summary>
    public bool RequireRunning { get; init; }
    /// <summary>Connect/read budget after device and channel admission; null uses the device timeout, capped at 60 s.</summary>
    public int? TimeoutMs { get; init; }
    public string Source { get; init; } = "local";
}

public sealed record DeviceProbeResult
{
    public Guid OperationId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid? TagId { get; init; }
    public string DeviceName { get; init; } = "";
    public string DriverId { get; init; } = "";
    public TagDataType? DataType { get; init; }
    public DeviceProbeState State { get; init; }
    public bool UsedRunningSession { get; init; }
    public bool Connected { get; init; }
    /// <summary>A fresh response to this operation, never a cached acquisition sample.</summary>
    public TelemetrySample? Sample { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string Source { get; init; } = "local";
    public string? Error { get; init; }
    public bool Success => State is DeviceProbeState.Succeeded or DeviceProbeState.ConnectedOnly;
    public SampleQuality Quality => Sample?.Quality
        ?? (State is DeviceProbeState.Failed or DeviceProbeState.TimedOut ? SampleQuality.Bad : SampleQuality.Uncertain);
}

public sealed record WriteVerificationOptions
{
    public bool VerifyReadBack { get; init; } = true;
    /// <summary>Total write/readback budget after device/channel admission, between 50 and 60000 ms.</summary>
    public int TimeoutMs { get; init; } = 5000;
    public int MaxAttempts { get; init; } = 5;
    public int RetryIntervalMs { get; init; } = 200;
    /// <summary>Engineering units. Nonnegative finite values only; zero requires exact equality.</summary>
    public double AbsoluteTolerance { get; init; }
    /// <summary>Relative to max(abs(expected), abs(actual)); must be between zero and one.</summary>
    public double RelativeTolerance { get; init; }
    public string Source { get; init; } = "local";
    public Guid? CommandId { get; init; }
    /// <summary>Additionally enforce the currently applied MQTT/RPC/remote-write permission under the device gate.</summary>
    public bool RequireRemoteWritePermission { get; init; }
    /// <summary>Additionally enforce the currently applied OPC UA user-authentication and write permissions under the device gate.</summary>
    public bool RequireOpcUaWritePermission { get; init; }
}

public sealed record WriteCommandResult
{
    public Guid CommandId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid TagId { get; init; }
    public WriteCommandState State { get; init; }
    /// <summary>The driver write was invoked. A failed or timed-out invocation may still have affected the device.</summary>
    public bool WriteAttempted { get; init; }
    /// <summary>The driver completed its write successfully; this alone is not a readback confirmation.</summary>
    public bool Submitted { get; init; }
    public bool VerificationRequested { get; init; }
    public string RequestedEngineeringValue { get; init; } = "";
    public object? WrittenRawValue { get; init; }
    /// <summary>The engineering value represented by WrittenRawValue, including the configured wire type's rounding.</summary>
    public object? ExpectedEngineeringValue { get; init; }
    public TelemetrySample? ReadBack { get; init; }
    public int ReadBackAttempts { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string Source { get; init; } = "local";
    public string? Error { get; init; }
}
