using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace EdgeGateway.Model;

/// <summary>The shared configuration and exchange format, independent of its storage location.</summary>
public static class GatewayJson
{
    // Standalone UTF-8 JSON output; retain required quote/control-character escaping.
    public static JavaScriptEncoder TextEncoder { get; } = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    // Transport defaults: do not change property casing, enum representation or indentation.
    public static JsonSerializerOptions TextOptions { get; } = new() { Encoder = TextEncoder };

    public static JsonSerializerOptions Options { get; } = new()
    {
        Encoder = TextEncoder,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;
}
