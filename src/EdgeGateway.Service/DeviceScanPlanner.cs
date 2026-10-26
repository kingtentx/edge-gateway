using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>
/// Fixed-rate scan deadlines on the device session's monotonic clock (first deadline: zero).
/// The sequential device worker owns this planner. Missed periods are skipped, never queued
/// as catch-up reads. Sample timestamps remain the actual acquisition time, not these deadlines.
/// </summary>
public sealed class DeviceScanPlanner(DeviceConfiguration device)
{
    private readonly Dictionary<Guid, long> _due = [];

    public IReadOnlyList<TagConfiguration> GetDue(long elapsedMilliseconds) => device.Tags.Where(t => t.Enabled
        && elapsedMilliseconds >= _due.GetValueOrDefault(t.Id)).ToArray();

    /// <summary>
    /// Advance only the attempted subset, after reading AND handing its results to the output
    /// channel. The next deadline stays on the previous planned grid, not completion + interval.
    /// A deadline exactly at completion is eligible; only deadlines strictly in the past are skipped.
    /// </summary>
    public void Complete(IReadOnlyList<TagConfiguration> tags, long elapsedMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        foreach (var tag in tags)
        {
            var interval = Interval(tag);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
            var next = checked(_due.GetValueOrDefault(tag.Id) + interval);
            if (next < elapsedMilliseconds)
            {
                // Round forward to the original grid in O(1), even after a long reconnect
                // or a blocked channel. No loop over missed periods and no phase drift.
                var remainder = (elapsedMilliseconds - next) % interval;
                next = checked(elapsedMilliseconds + (remainder == 0 ? 0 : interval - remainder));
            }
            _due[tag.Id] = next;
        }
    }

    public int DelayUntilNext(long elapsedMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        var enabled = device.Tags.Where(t => t.Enabled).ToArray();
        if (enabled.Length == 0) return device.PollIntervalMs;
        var next = enabled.Min(t => _due.GetValueOrDefault(t.Id));
        // An overdue different-rate tag or an exact-boundary deadline is eligible now.
        // Complete always advances a consumed deadline, so zero delay cannot replay it.
        return (int)Math.Clamp(next - elapsedMilliseconds, 0, 3_600_000);
    }

    private int Interval(TagConfiguration tag) => device.ScanMode == DeviceScanMode.TagRate && tag.ScanIntervalMs > 0
        ? tag.ScanIntervalMs : device.PollIntervalMs;
}
