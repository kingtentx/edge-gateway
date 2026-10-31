using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

/// <summary>View-only retention; never publishes held values back into the acquisition runtime.</summary>
internal sealed class RealtimeDisplayState
{
    private readonly Dictionary<(Guid Device, Guid Tag), TelemetrySample> _held = [];

    internal IReadOnlyList<RealtimeDisplayPoint> Update(IReadOnlyList<DeviceConfiguration> devices,
        IReadOnlyList<DeviceRuntimeStatus> statuses, IReadOnlyList<TelemetrySample> samples)
    {
        var configured = devices.SelectMany(device => device.Tags.Select(tag => (device.Id, tag.Id))).ToHashSet();
        foreach (var key in _held.Keys.Where(key => !configured.Contains(key)).ToArray()) _held.Remove(key);
        var latest = samples.GroupBy(sample => (sample.DeviceId, sample.TagId))
            .ToDictionary(group => group.Key, group => group.MaxBy(sample => sample.Timestamp)!);
        var states = statuses.ToDictionary(status => status.DeviceId);
        var result = new List<RealtimeDisplayPoint>();
        foreach (var device in devices)
        {
            var state = states.GetValueOrDefault(device.Id);
            var online = device.Enabled && state?.State == DeviceState.Running;
            foreach (var tag in device.Tags)
            {
                var key = (device.Id, tag.Id);
                var incoming = latest.GetValueOrDefault(key);
                var held = _held.GetValueOrDefault(key);
                if (held?.Address != tag.Address) { _held.Remove(key); held = null; }
                if (incoming?.Address != tag.Address) incoming = null;
                // First view may seed the last known runtime sample even when stopped. Subsequent
                // offline updates cannot change values or advance their displayed sample time.
                if (incoming is not null && (held is null || online && tag.Enabled && incoming.Timestamp >= held.Timestamp))
                    _held[key] = held = incoming;
                var hasSample = held is not null;
                var display = held ?? new TelemetrySample(device.Id, device.Name, tag.Id, tag.Name, tag.Address,
                    null, null, SampleQuality.Uncertain, DateTimeOffset.MinValue, tag.Unit);
                if (!online || !tag.Enabled)
                {
                    var reason = !device.Enabled ? "设备未启用" : !tag.Enabled ? "点位未启用" : state?.State switch
                    {
                        DeviceState.Connecting => "设备连接中", DeviceState.Reconnecting => "设备重连中",
                        DeviceState.Faulted => "设备离线", _ => "设备已停止"
                    };
                    var error = incoming?.Error ?? state?.Error;
                    display = display with
                    {
                        Quality = incoming?.Quality == SampleQuality.Bad || display.Quality == SampleQuality.Bad
                            ? SampleQuality.Bad : SampleQuality.Uncertain,
                        Error = reason + (hasSample ? "；保留最后采样值" : "；尚无采样值")
                            + (string.IsNullOrWhiteSpace(error) ? "" : "；" + error)
                    };
                }
                else if (incoming is null)
                    display = display with { Quality = SampleQuality.Uncertain, Error = "等待新采样" };
                result.Add(new(display with { DeviceName = device.Name, TagName = tag.Name, Address = tag.Address, Unit = tag.Unit }, hasSample));
            }
        }
        return result;
    }
}

internal sealed record RealtimeDisplayPoint(TelemetrySample Sample, bool HasSample);
