using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>Platform wire contracts. Values, quality and source timestamps remain distinct.</summary>
public static class PlatformMqttAdapter
{
    public static IReadOnlyList<string> ValidateConfiguration(MqttConfiguration settings)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(settings.Platform)) errors.Add("MQTT 平台类型无效。");
        if (settings.RpcTimeoutMs is < 500 or > 60000) errors.Add("RPC 超时必须为 500–60000 ms。");
        if (!string.IsNullOrEmpty(settings.UsernameEnvironmentVariable) && !Regex.IsMatch(settings.UsernameEnvironmentVariable, "^[A-Za-z_][A-Za-z0-9_]*$"))
            errors.Add("MQTT 用户名/令牌只允许配置合法的环境变量名称。");
        if (settings.AllowRemoteWrite && !settings.RpcEnabled) errors.Add("允许远程写入前须启用 RPC。");
        if (!settings.RpcEnabled) return errors;
        if (settings.Platform == MqttPlatform.Generic)
        {
            if (!ValidTopic(settings.RpcRequestTopic) || !ValidTopic(settings.RpcResponseTopic))
                errors.Add("通用 RPC 请求与响应主题必须是无通配符、无空字符的有效 MQTT 主题。");
            if (settings.RpcRequestTopic == settings.RpcResponseTopic) errors.Add("通用 RPC 请求与响应主题必须不同。");
            if (settings.RpcRequestTopic == settings.Topic) errors.Add("RPC 请求主题不能与遥测主题相同。");
        }
        if (settings.Platform == MqttPlatform.ThingsPanel && !ValidSegment(settings.PlatformGatewayId))
            errors.Add("ThingsPanel RPC 需要有效的云端网关设备编号，不能含 /、+、# 或空字符。");
        return errors;
    }

    public static IReadOnlyList<string> ValidateGateway(GatewayConfiguration configuration)
    {
        if (configuration.Mqtt is null) return [];
        var errors = ValidateConfiguration(configuration.Mqtt).ToList();
        if (configuration.Mqtt.RpcEnabled && configuration.Mqtt.Platform == MqttPlatform.Generic)
        {
            var request = Expand(configuration.Mqtt.RpcRequestTopic ?? "", configuration.GatewayId ?? "");
            var response = Expand(configuration.Mqtt.RpcResponseTopic ?? "", configuration.GatewayId ?? "");
            var telemetry = Expand(configuration.Mqtt.Topic ?? "", configuration.GatewayId ?? "");
            if (!ValidTopic(request) || !ValidTopic(response)) errors.Add("展开网关标识后的 RPC 主题无效或超过 4000 字节。");
            if (request == response || request == telemetry) errors.Add("展开网关标识后，RPC 请求主题不能与响应或遥测主题相同。");
        }
        if (!configuration.Mqtt.Enabled || configuration.Mqtt.Platform == MqttPlatform.Generic) return errors;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in configuration.Devices ?? [])
        {
            if (device is null) continue;
            if (!names.Add(device.Name)) errors.Add($"专用平台要求设备名唯一：{device.Name}。");
            if (configuration.Mqtt.Platform == MqttPlatform.IoTSharp && !ValidSegment(device.Name))
                errors.Add($"IoTSharp 设备名不能含 /、+、# 或空字符：{device.Name}。");
            foreach (var tag in device.Tags ?? [])
                if (tag is not null && string.Equals(tag.Name, "_edge", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"设备 [{device.Name}] 点位名 _edge 为平台质量与时间戳元数据保留名称。");
        }
        return errors;
    }

    public static IReadOnlyList<MqttOutboundMessage> BuildTelemetry(GatewayConfiguration configuration,
        IReadOnlyList<TelemetrySample> samples, string messageId, DateTimeOffset publishedAt)
    {
        if (samples.Count == 0) return [];
        if (configuration.Mqtt.Platform == MqttPlatform.Generic)
            return [Json(Expand(configuration.Mqtt.Topic, configuration.GatewayId), new
            {
                schemaVersion = 1, messageId, gatewayId = configuration.GatewayId, timestamp = publishedAt,
                samples = samples.Select(sample => new
                {
                    deviceId = sample.DeviceId, deviceName = sample.DeviceName, tagId = sample.TagId, tagName = sample.TagName,
                    address = sample.Address, rawValue = sample.RawValue, value = sample.Value, unit = sample.Unit,
                    quality = sample.Quality.ToString(), timestamp = sample.Timestamp, error = sample.Error
                })
            })];
        var devices = samples.GroupBy(sample => sample.DeviceName, StringComparer.Ordinal).ToArray();
        object Values(IEnumerable<TelemetrySample> source) => ValueMap(source.ToArray(), configuration.GatewayId, messageId, publishedAt);
        object Series(IEnumerable<TelemetrySample> source) => source.GroupBy(sample => sample.Timestamp.ToUnixTimeMilliseconds())
            .Select(group => new { ts = group.Key, values = Values(group) }).ToArray();
        return configuration.Mqtt.Platform switch
        {
            MqttPlatform.ThingsBoard => [Json("v1/gateway/telemetry", devices.ToDictionary(group => group.Key, group => Series(group), StringComparer.Ordinal))],
            MqttPlatform.ThingsCloud => devices.Select(group => Json("gateway/attributes", new Dictionary<string, object> { [group.Key] = Values(group) })).ToArray(),
            MqttPlatform.ThingsPanel => [Json("gateway/telemetry", new
            { sub_device_data = devices.ToDictionary(group => group.Key, group => Series(group), StringComparer.Ordinal) })],
            MqttPlatform.IoTSharp => devices.Select(group => Json($"devices/{group.Key}/telemetry", Values(group))).ToArray(),
            _ => throw new InvalidOperationException("MQTT 平台类型无效。")
        };
    }

    private static Dictionary<string, object?> ValueMap(IReadOnlyList<TelemetrySample> samples, string gatewayId, string messageId, DateTimeOffset publishedAt)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var sample in samples)
            if (sample.Quality == SampleQuality.Good && sample.Value is not null) values.Add(sample.TagName, sample.Value);
        // Bad and Uncertain samples deliberately have no ordinary value key: a platform must not
        // mistake a stopped/failed read for a fresh valid measurement. Metadata still reports the transition.
        values.Add("_edge", new
        {
            messageId, gatewayId, publishedAt,
            tags = samples.ToDictionary(sample => sample.TagName, sample => new
            { quality = sample.Quality.ToString(), timestamp = sample.Timestamp, error = sample.Error }, StringComparer.Ordinal)
        });
        return values;
    }

    public static IReadOnlyList<string> GetRpcSubscriptions(GatewayConfiguration configuration) => !configuration.Mqtt.RpcEnabled ? [] : configuration.Mqtt.Platform switch
    {
        MqttPlatform.Generic => [Expand(configuration.Mqtt.RpcRequestTopic, configuration.GatewayId)],
        MqttPlatform.ThingsBoard => ["v1/gateway/rpc"],
        MqttPlatform.ThingsCloud => ["gateway/command/send"],
        MqttPlatform.ThingsPanel => [$"gateway/command/{configuration.Mqtt.PlatformGatewayId}/+"],
        MqttPlatform.IoTSharp => ["devices/+/rpc/request/+/+"],
        _ => []
    };

    public static MqttOutboundMessage? DevicePresence(MqttConfiguration configuration, string deviceName, bool connected) => configuration.Platform switch
    {
        MqttPlatform.ThingsBoard when connected => Json("v1/gateway/connect", new { device = deviceName, type = configuration.PlatformDeviceType }),
        MqttPlatform.ThingsBoard => Json("v1/gateway/disconnect", new { device = deviceName }),
        MqttPlatform.ThingsCloud when connected && !string.IsNullOrWhiteSpace(configuration.ThingsCloudTypeKey) =>
            Json("gateway/connect", new { device = deviceName, type_key = configuration.ThingsCloudTypeKey }),
        MqttPlatform.ThingsCloud => Json(connected ? "gateway/connect" : "gateway/disconnect", new { device = deviceName }),
        _ => null
    };

    public static PlatformRpcRequest? ParseRpc(GatewayConfiguration configuration, MqttInboundMessage message)
    {
        if (message.Retained || Encoding.UTF8.GetByteCount(message.Payload) > MqttTelemetryPublisher.MaximumRpcPayloadBytes)
            throw new InvalidDataException("保留命令或超过 64 KiB 的 RPC 载荷被拒绝。");
        using var document = JsonDocument.Parse(message.Payload, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        RequireObject(root);
        JsonElement command;
        object id;
        string device;
        string method;
        string replyTopic;
        switch (configuration.Mqtt.Platform)
        {
            case MqttPlatform.Generic:
                if (message.Topic != Expand(configuration.Mqtt.RpcRequestTopic, configuration.GatewayId)) return null;
                command = root;
                id = RequestId(root.GetProperty("id"));
                device = String(root, "deviceId", "device");
                method = String(command, "method");
                replyTopic = Expand(configuration.Mqtt.RpcResponseTopic, configuration.GatewayId);
                break;
            case MqttPlatform.ThingsBoard:
                if (message.Topic != "v1/gateway/rpc") return null;
                command = root.GetProperty("data");
                // ThingsBoard request and reply use one topic; ignore our own response echo.
                if (!command.TryGetProperty("method", out _)) return null;
                id = RequestId(command.GetProperty("id"));
                device = String(root, "device");
                method = String(command, "method");
                replyTopic = message.Topic;
                break;
            case MqttPlatform.ThingsCloud:
                if (message.Topic != "gateway/command/send") return null;
                command = root.GetProperty("command");
                id = RequestId(command.GetProperty("id"));
                device = String(root, "device");
                method = String(command, "method");
                replyTopic = "gateway/command/reply";
                break;
            case MqttPlatform.ThingsPanel:
                var prefix = $"gateway/command/{configuration.Mqtt.PlatformGatewayId}/";
                if (!message.Topic.StartsWith(prefix, StringComparison.Ordinal)) return null;
                id = Segment(message.Topic[prefix.Length..]);
                var children = root.GetProperty("sub_device_data");
                RequireObject(children);
                var childList = children.EnumerateObject().ToArray();
                if (childList.Length != 1 || root.TryGetProperty("gateway_data", out _) || root.TryGetProperty("sub_gateway_data", out _))
                    throw new InvalidDataException("每条 ThingsPanel 读写命令必须且只能指定一个直接子设备。");
                device = childList[0].Name;
                command = childList[0].Value;
                method = String(command, "method");
                replyTopic = $"gateway/command/response/{id}";
                break;
            case MqttPlatform.IoTSharp:
                var parts = message.Topic.Split('/');
                if (parts.Length != 6 || parts[0] != "devices" || parts[2] != "rpc" || parts[3] != "request") return null;
                device = ValidSegment(parts[1]) ? parts[1] : throw new InvalidDataException("RPC 设备主题段无效。");
                method = Segment(parts[4]);
                id = Segment(parts[5]);
                command = JsonSerializer.SerializeToElement(new { method, @params = root });
                replyTopic = $"devices/{device}/rpc/response/{method}/{id}";
                break;
            default: return null;
        }
        RequireObject(command);
        var parameters = command.GetProperty("params");
        RequireObject(parameters);
        var tag = String(parameters, "tagId", "tag");
        if (device.Length > 200 || tag.Length > 200 || method.Length > 32) throw new InvalidDataException("RPC 目标或方法名称过长。");
        JsonElement? value = parameters.TryGetProperty("value", out var supplied) ? supplied.Clone() : null;
        if (parameters.TryGetProperty("expiresAt", out var expiry))
        {
            if (!DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
                || expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddMinutes(10))
                throw new InvalidDataException("RPC expiresAt 已过期或超过未来 10 分钟。");
        }
        return new(configuration.Mqtt.Platform, Convert.ToString(id, CultureInfo.InvariantCulture)!, id, device, tag,
            method, value, replyTopic, message.ReceivedAt);
    }

    public static MqttOutboundMessage RpcReply(PlatformRpcRequest request, RpcCommandResponse result) => request.Platform switch
    {
        MqttPlatform.ThingsBoard => Json(request.ReplyTopic, new { device = request.Device, id = request.WireId, data = result }),
        MqttPlatform.ThingsCloud => Json(request.ReplyTopic, new { device = request.Device, command = new { method = request.Method, id = request.WireId, @params = result } }),
        MqttPlatform.ThingsPanel => Json(request.ReplyTopic, new
        { result = result.success ? 0 : 1, message = result.error ?? result.status, errcode = result.success ? null : result.status,
            method = request.Method, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), data = result }),
        MqttPlatform.IoTSharp => Json(request.ReplyTopic, new { DeviceId = request.Device, Method = request.Method, ResponseId = request.Id, Data = JsonSerializer.Serialize(result) }),
        _ => Json(request.ReplyTopic, new { id = request.WireId, device = request.Device, data = result })
    };

    private static string String(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
            if (element.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()))
                return property.GetString()!;
        throw new InvalidDataException($"RPC 缺少字符串字段 {string.Join('/', keys)}。");
    }
    private static object RequestId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) && number >= 0) return number;
        if (element.ValueKind == JsonValueKind.String) return Segment(element.GetString()!);
        throw new InvalidDataException("RPC id 必须是非负整数或 1–128 位安全标识符。");
    }
    private static string Segment(string text) => Regex.IsMatch(text, "^[A-Za-z0-9._:-]{1,128}$") ? text : throw new InvalidDataException("RPC 请求标识或主题段无效。");
    private static void RequireObject(JsonElement element)
    { if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("RPC 消息和参数必须是 JSON 对象。"); }
    private static bool ValidSegment(string text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 200 && text.IndexOfAny(['/', '+', '#', '\0']) < 0;
    private static bool ValidTopic(string text) => !string.IsNullOrWhiteSpace(text) && Encoding.UTF8.GetByteCount(text) <= 4000 && text.IndexOfAny(['+', '#', '\0']) < 0;
    private static string Expand(string topic, string gatewayId) => topic.Replace("{gatewayId}", gatewayId, StringComparison.Ordinal);
    private static MqttOutboundMessage Json(string topic, object payload) => new(topic, JsonSerializer.Serialize(payload));
}

public sealed record PlatformRpcRequest(MqttPlatform Platform, string Id, object WireId, string Device,
    string Tag, string Method, JsonElement? Value, string ReplyTopic, DateTimeOffset ReceivedAt);

public sealed record RpcCommandResponse(bool success, string status, string? error = null, object? data = null, Guid? commandId = null);
