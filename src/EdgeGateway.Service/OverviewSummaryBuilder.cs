using EdgeGateway.Model;

namespace EdgeGateway.Service;

public static class OverviewSummaryBuilder
{
    public static GatewayOverviewSummary Build(GatewayConfiguration configuration, IReadOnlyList<DeviceRuntimeStatus> statuses,
        IReadOnlyList<TelemetrySample> samples, IReadOnlySet<Guid> activeDevices)
    {
        var byId = statuses.ToDictionary(status => status.DeviceId);
        var enabled = configuration.Devices.Where(device => device.Enabled).ToArray();
        DeviceState State(DeviceConfiguration device) => byId.GetValueOrDefault(device.Id)?.State ?? DeviceState.Stopped;
        var online = enabled.Count(device => State(device) == DeviceState.Running);
        var connecting = enabled.Count(device => State(device) == DeviceState.Connecting);
        var reconnecting = enabled.Count(device => State(device) == DeviceState.Reconnecting);
        var faulted = enabled.Count(device => State(device) == DeviceState.Faulted);
        var abnormal = enabled.Count(device => State(device) is DeviceState.Reconnecting or DeviceState.Faulted
            || State(device) == DeviceState.Running && !string.IsNullOrEmpty(byId[device.Id].Error));
        var tags = enabled.SelectMany(device => device.Tags.Where(tag => tag.Enabled).Select(tag => (tag.Id, device.Id)))
            .ToDictionary(pair => pair.Item1, pair => pair.Item2);
        var current = samples.Where(sample => tags.TryGetValue(sample.TagId, out var device) && device == sample.DeviceId
            && activeDevices.Contains(device)).ToArray();
        return new(configuration.Devices.Count, enabled.Length, online, abnormal,
            configuration.Devices.Count - online - connecting - reconnecting - faulted,
            connecting, reconnecting, faulted, tags.Count,
            current.Count(sample => sample.Quality == SampleQuality.Good), current.Count(sample => sample.Quality == SampleQuality.Bad),
            current.Count(sample => sample.Quality == SampleQuality.Uncertain), tags.Count - current.Length);
    }
}
