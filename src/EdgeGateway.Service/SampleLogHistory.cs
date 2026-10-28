using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public static class SampleLogHistory
{
    public static SampleLogHistoryResult FromArchive(GatewayLogQueryResult result)
    {
        var samples = new List<TelemetrySample>();
        foreach (var entry in result.Entries)
        {
            if (entry.Sample is not { } sample) continue;
            using var document = JsonDocument.Parse(entry.Json);
            var data = document.RootElement.TryGetProperty("sample", out var nested) ? nested : document.RootElement;
            object? Value(string name)
            {
                if (!data.TryGetProperty(name, out var value)
                    && !data.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out value)) return null;
                return value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => value.GetString(),
                    _ => value.Clone()
                };
            }
            samples.Add(new(sample.DeviceId, sample.DeviceName, sample.TagId, sample.TagName,
                sample.Address, Value("rawValue"), Value("value"), sample.Quality, entry.Timestamp, sample.Unit, sample.Error));
        }
        return new(samples, result.ScannedBytes, result.InvalidLines, result.IsPartial, result.Warnings);
    }
}
