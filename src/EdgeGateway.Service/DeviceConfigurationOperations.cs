using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public static class DeviceConfigurationOperations
{
    /// <summary>Creates an isolated disabled draft. The normal editor validates addresses and resource conflicts before save.</summary>
    public static DeviceConfiguration Copy(DeviceConfiguration source, IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = JsonSerializer.Deserialize<DeviceConfiguration>(JsonSerializer.Serialize(source))!;
        copy.Id = Guid.NewGuid();
        foreach (var tag in copy.Tags) tag.Id = Guid.NewGuid();
        copy.Enabled = false;
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = source.Name + " 副本";
        copy.Name = stem;
        for (var index = 2; names.Contains(copy.Name); index++) copy.Name = stem + " " + index;
        return copy;
    }

    public static IReadOnlyList<DeviceConfiguration> Filter(IEnumerable<DeviceConfiguration> devices,
        string? group = null, string? search = null, Guid? channelId = null, bool independentOnly = false)
    {
        var query = devices;
        if (group is not null) query = query.Where(d => string.Equals(d.Group, group, StringComparison.OrdinalIgnoreCase));
        if (independentOnly) query = query.Where(d => d.ChannelId is null);
        else if (channelId.HasValue) query = query.Where(d => d.ChannelId == channelId);
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term)) query = query.Where(d => d.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || d.DriverId.Contains(term, StringComparison.OrdinalIgnoreCase)
            || d.Group.Contains(term, StringComparison.OrdinalIgnoreCase)
            || d.Tags.Any(t => t.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || t.Address.Contains(term, StringComparison.OrdinalIgnoreCase)));
        return query.ToArray();
    }
}
