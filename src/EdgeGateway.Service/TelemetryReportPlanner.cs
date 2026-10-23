using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>A baseline advances only after the selected snapshot is durably queued, never just on a read.</summary>
public sealed class TelemetryReportPlanner
{
    private readonly Dictionary<Guid, Baseline> _baselines = [];
    private sealed record Baseline(JsonElement Value, SampleQuality Quality, string? Error, DateTimeOffset EnqueuedAt);

    public IReadOnlyList<TelemetrySample> Select(GatewayConfiguration configuration,
        IReadOnlyList<TelemetrySample> samples, DateTimeOffset now)
    {
        var tags = configuration.Devices.Where(d => d.Enabled)
            .SelectMany(d => d.Tags.Where(t => t.Enabled && t.UploadEnabled).Select(t => (d.Id, Tag: t)))
            .ToDictionary(x => x.Tag.Id);
        var selected = new List<TelemetrySample>();
        foreach (var sample in samples)
        {
            if (!tags.TryGetValue(sample.TagId, out var entry) || entry.Id != sample.DeviceId) continue;
            if (configuration.ReportMode == TelemetryReportMode.Periodic
                || !_baselines.TryGetValue(sample.TagId, out var baseline)
                || sample.Quality != baseline.Quality || sample.Error != baseline.Error
                || !ValuesWithinDeadband(JsonSerializer.SerializeToElement(sample.Value), baseline.Value, entry.Tag.Deadband)
                || configuration.ReportMode == TelemetryReportMode.OnChangeWithHeartbeat
                   && now - baseline.EnqueuedAt >= TimeSpan.FromMilliseconds(configuration.HeartbeatIntervalMs))
                selected.Add(sample);
        }
        return selected;
    }

    public void Commit(IReadOnlyList<TelemetrySample> samples, DateTimeOffset enqueuedAt)
    {
        foreach (var sample in samples)
            _baselines[sample.TagId] = new(JsonSerializer.SerializeToElement(sample.Value), sample.Quality, sample.Error, enqueuedAt);
    }

    public void Reset() => _baselines.Clear();
    public void Forget(IEnumerable<Guid> tagIds)
    {
        foreach (var tagId in tagIds) _baselines.Remove(tagId);
    }

    /// <summary>Numeric deadband is absolute in engineering units; boolean/string/object types stay distinct.</summary>
    public static bool ValuesWithinDeadband(JsonElement first, JsonElement second, double deadband = 0)
    {
        if (first.ValueKind != second.ValueKind) return false;
        switch (first.ValueKind)
        {
            case JsonValueKind.Number:
                // Compare decimal coefficients and exponents without forcing Int64/UInt64
                // through double or rounding tiny scientific values to decimal's scale 28.
                return ExactNumericValue.TryParse(first.GetRawText(), out var a)
                    && ExactNumericValue.TryParse(second.GetRawText(), out var b)
                    && ExactNumericValue.TryParse(deadband, out var tolerance)
                    && ExactNumericValue.Compare(ExactNumericValue.Distance(a, b), tolerance) <= 0;
            case JsonValueKind.String: return first.GetString() == second.GetString();
            case JsonValueKind.Array:
                return first.GetArrayLength() == second.GetArrayLength()
                    && first.EnumerateArray().Zip(second.EnumerateArray()).All(p => ValuesWithinDeadband(p.First, p.Second));
            case JsonValueKind.Object:
                var properties = first.EnumerateObject().ToArray();
                return properties.Length == second.EnumerateObject().Count()
                    && properties.All(p => second.TryGetProperty(p.Name, out var other) && ValuesWithinDeadband(p.Value, other));
            default: return true;
        }
    }
}
