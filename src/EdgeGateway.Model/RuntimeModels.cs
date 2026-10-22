using System.Globalization;

namespace EdgeGateway.Model;

public sealed record DriverDescriptor(string Id, string Name, string Description, bool SupportsWrite);
public sealed record TagReadResult(Guid TagId, object? Value, SampleQuality Quality = SampleQuality.Good, string? Error = null);
public sealed record TelemetrySample(Guid DeviceId, string DeviceName, Guid TagId, string TagName,
    string Address, object? RawValue, object? Value, SampleQuality Quality, DateTimeOffset Timestamp,
    string Unit, string? Error = null)
{
    public double? NumericValue
    {
        get
        {
            if (Value is null or bool) return null;
            return double.TryParse(Convert.ToString(Value, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
        }
    }
}
public sealed record DeviceRuntimeStatus(Guid DeviceId, string DeviceName, DeviceState State,
    DateTimeOffset? LastSuccess, int ConsecutiveFailures, string? Error);
public sealed record GatewayLogEntry(DateTimeOffset Timestamp, GatewayLogLevel Level, string Message);
public sealed record OutboxMessage(long Id, string Topic, string Payload, DateTimeOffset CreatedAt);
public sealed record StoreStatistics(long HistoryCount, long OutboxCount);
public sealed record HistoryQuery(Guid? DeviceId, Guid? TagId, DateTimeOffset From, DateTimeOffset To, int Limit = 5000);
