namespace EdgeGateway.Service;

/// <summary>
/// Device identities affected by a durably committed configuration. Restarted means
/// a fresh collection session was scheduled, not that the device has connected yet.
/// Stopped contains previously running devices that were removed or disabled, and
/// excludes devices already listed in Restarted. Added devices remain stopped.
/// </summary>
public sealed record ConfigurationApplyResult(
    IReadOnlyList<Guid> Added,
    IReadOnlyList<Guid> Updated,
    IReadOnlyList<Guid> Removed,
    IReadOnlyList<Guid> Restarted,
    IReadOnlyList<Guid> Stopped);
