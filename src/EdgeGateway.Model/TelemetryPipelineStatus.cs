namespace EdgeGateway.Model;

/// <summary>
/// Immutable, process-local northbound diagnostics. Times are UTC. A broker QoS 1
/// acknowledgement does not confirm platform processing; retries can be counted again.
/// Historical event times and acknowledgement totals survive device configuration changes.
/// </summary>
public sealed record TelemetryPipelineStatus
{
    public bool Enabled { get; init; }
    public bool Running { get; init; }
    public bool Sending { get; init; }
    public DateTimeOffset? LastEnqueueAttemptAt { get; init; }
    public DateTimeOffset? LastEnqueuedAt { get; init; }
    public DateTimeOffset? LastSendAttemptAt { get; init; }
    public DateTimeOffset? LastBrokerAcknowledgedAt { get; init; }
    public DateTimeOffset? LastSendFailureAt { get; init; }
    public DateTimeOffset? LastPersistenceFailureAt { get; init; }
    public DateTimeOffset? LastCapacityBlockedAt { get; init; }
    public long AcknowledgedMessages { get; init; }
    /// <summary>Consecutive failures of the outbox read, publish, or acknowledgement-removal pipeline.</summary>
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public string? SendError { get; init; }
    public bool QueueCapacityBlocked { get; init; }
    /// <summary>Current queue generation/storage errors, separate from broker communication failures.</summary>
    public string? PersistenceError { get; init; }
}
