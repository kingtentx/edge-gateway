using EdgeGateway.Model;

namespace EdgeGateway.Application;

/// <summary>Pure editor operations use the same implementations as runtime validation.</summary>
public static class ConfigurationValidator
{
    public static IReadOnlyList<string> Validate(GatewayConfiguration candidate, IDriverCatalog catalog) => catalog.ValidateConfiguration(candidate);
}

public static class DatabaseMappingTools
{
    public static DatabaseFieldType InferFieldType(string databaseType) => Service.DatabaseMappingRules.InferFieldType(databaseType);
    public static string? ValidateValue(DatabaseColumnMapping mapping) => Service.DatabaseMappingRules.ValidateValue(mapping);
}

public static class NumericExpression
{
    public const int MaximumLength = Service.NumericExpression.MaximumLength;
    public static string? Validate(string? expression) => Service.NumericExpression.Validate(expression);
    public static ExpressionPreview Preview(string? expression, string engineeringValue) => Service.NumericExpression.Preview(expression, engineeringValue);
    public static decimal Evaluate(string? expression, decimal value) => Service.NumericExpression.Evaluate(expression, value);
}

public static class TagCsvExchange
{
    public const int MaxFileBytes = Service.TagCsvExchange.MaxFileBytes;
    public const int MaxTagsPerDevice = Service.TagCsvExchange.MaxTagsPerDevice;
    public const int MaxDataRows = Service.TagCsvExchange.MaxDataRows;
    public static IReadOnlyList<string> Columns => Service.TagCsvExchange.Columns;
    public static byte[] Export(IEnumerable<TagConfiguration> tags) => Service.TagCsvExchange.Export(tags);
    public static TagCsvImportResult Import(byte[] content, IReadOnlyList<TagConfiguration> existingTags,
        TagCsvImportMode mode = TagCsvImportMode.Append) => Service.TagCsvExchange.Import(content, existingTags, mode);
}

public static class DemoConfiguration
{
    public static GatewayConfiguration Create() => Service.DemoConfiguration.Create();
    public static DeviceConfiguration CreateModbusRtuExample() => Service.DemoConfiguration.CreateModbusRtuExample();
}

public static class DeviceConfigurationOperations
{
    public static DeviceConfiguration Copy(DeviceConfiguration source, IEnumerable<string> existingNames) =>
        Service.DeviceConfigurationOperations.Copy(source, existingNames);
    public static IReadOnlyList<DeviceConfiguration> Filter(IEnumerable<DeviceConfiguration> devices,
        string? group = null, string? search = null, Guid? channelId = null, bool independentOnly = false) =>
        Service.DeviceConfigurationOperations.Filter(devices, group, search, channelId, independentOnly);
}
