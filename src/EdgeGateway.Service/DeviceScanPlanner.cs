using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>Uses elapsed monotonic milliseconds; no accumulated catch-up reads after a slow or failed request.</summary>
public sealed class DeviceScanPlanner(DeviceConfiguration device)
{
    private readonly Dictionary<Guid, long> _due = [];

    public IReadOnlyList<TagConfiguration> GetDue(long elapsedMilliseconds) => device.Tags.Where(t => t.Enabled
        && (!_due.TryGetValue(t.Id, out var due) || elapsedMilliseconds >= due)).ToArray();

    public void Complete(IReadOnlyList<TagConfiguration> tags, long elapsedMilliseconds)
    {
        foreach (var tag in tags)
            _due[tag.Id] = elapsedMilliseconds + Interval(tag);
    }

    public int DelayUntilNext(long elapsedMilliseconds)
    {
        var enabled = device.Tags.Where(t => t.Enabled).ToArray();
        if (enabled.Length == 0) return device.PollIntervalMs;
        var next = enabled.Min(t => _due.GetValueOrDefault(t.Id, elapsedMilliseconds));
        return (int)Math.Clamp(next - elapsedMilliseconds, 1, 3_600_000);
    }

    private int Interval(TagConfiguration tag) => device.ScanMode == DeviceScanMode.TagRate && tag.ScanIntervalMs > 0
        ? tag.ScanIntervalMs : device.PollIntervalMs;
}
