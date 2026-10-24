using System.Globalization;

namespace EdgeGateway.Model;

public sealed record DriverDescriptor(string Id, string Name, string Description, bool SupportsWrite)
{
    public bool IsLegacy { get; init; }
    public IReadOnlyList<DriverParameterDescriptor> Parameters { get; init; } = [];
    public IReadOnlyList<DriverMethodDescriptor> Methods { get; init; } = [];
    public IReadOnlyList<TagDataType> SupportedDataTypes { get; init; } = [];
    public string DefaultReadMethod { get; init; } = "";
    public string Prerequisites { get; init; } = "";
    public bool RequiresSta { get; init; }
}
public sealed record DriverParameterDescriptor(string Key, string Name, string Description, string TypeName,
    string DefaultValue, IReadOnlyList<string> Options, bool IsReadOnly = false, bool IsSecret = false)
{
    public string? BoundProperty { get; init; }
}
public sealed record DriverMethodDescriptor(string Name, string DisplayName, string Description, bool SupportsWrite = false)
{
    public IReadOnlyList<TagDataType> SupportedDataTypes { get; init; } = [];
    public string DefaultAddress { get; init; } = "";
    public TagDataType DefaultDataType { get; init; } = TagDataType.Any;
    public bool IsDiagnostic { get; init; }
}
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
public sealed record StoreStatistics(long OutboxCount, DateTimeOffset? OldestOutboxAt = null);
public sealed record HistoryQuery(Guid? DeviceId, Guid? TagId, DateTimeOffset From, DateTimeOffset To, int Limit = 5000);
