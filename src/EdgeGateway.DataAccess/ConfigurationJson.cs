using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

internal static class ConfigurationJson
{
    public static GatewayConfiguration Deserialize(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("配置内容必须为 JSON 对象，并显式声明 schemaVersion=3。");

        var propertyName = GatewayJson.Options.PropertyNamingPolicy?.ConvertName(nameof(GatewayConfiguration.SchemaVersion))
            ?? nameof(GatewayConfiguration.SchemaVersion);
        var comparison = GatewayJson.Options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var declaredVersion = default(JsonElement);
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, propertyName, comparison)) declaredVersion = property.Value;

        if (declaredVersion.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("配置缺少 schemaVersion；必须显式声明 schemaVersion=3。");
        if (declaredVersion.ValueKind != JsonValueKind.Number || !declaredVersion.TryGetInt32(out var version))
            throw new InvalidDataException("配置中的 schemaVersion 必须为整数 3。");
        if (version != 3)
            throw new InvalidDataException($"配置版本 {version} 不受支持；仅支持版本 3。");

        return root.Deserialize<GatewayConfiguration>(GatewayJson.Options)
            ?? throw new InvalidDataException("配置内容为空。");
    }
}
