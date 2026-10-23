using System.Text.Json;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class PlatformAdapterTests
{
    [Theory]
    [InlineData(MqttPlatform.Generic, "edge/edge-001/telemetry")]
    [InlineData(MqttPlatform.ThingsBoard, "v1/gateway/telemetry")]
    [InlineData(MqttPlatform.ThingsCloud, "gateway/attributes")]
    [InlineData(MqttPlatform.ThingsPanel, "gateway/telemetry")]
    [InlineData(MqttPlatform.IoTSharp, "devices/pump/telemetry")]
    public void PlatformTelemetryHasExpectedTopicTypedValueAndQualityMetadata(MqttPlatform platform, string expectedTopic)
    {
        var config = new GatewayConfiguration { Mqtt = new() { Platform = platform } };
        var now = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        TelemetrySample[] samples = [new(Guid.NewGuid(), "pump", Guid.NewGuid(), "counter", "0", ulong.MaxValue, ulong.MaxValue, SampleQuality.Good, now, ""),
            new(Guid.NewGuid(), "pump", Guid.NewGuid(), "failed", "1", null, null, SampleQuality.Bad, now, "", "offline")];
        var message = Assert.Single(PlatformMqttAdapter.BuildTelemetry(config, samples, "msg-1", now));
        Assert.Equal(expectedTopic, message.Topic);
        using var document = JsonDocument.Parse(message.Payload);
        var root = document.RootElement;
        if (platform == MqttPlatform.Generic)
        {
            Assert.Equal(ulong.MaxValue, root.GetProperty("samples")[0].GetProperty("value").GetUInt64());
            Assert.Equal("Bad", root.GetProperty("samples")[1].GetProperty("quality").GetString());
            return;
        }
        var values = platform switch
        {
            MqttPlatform.ThingsBoard => root.GetProperty("pump")[0].GetProperty("values"),
            MqttPlatform.ThingsCloud => root.GetProperty("pump"),
            MqttPlatform.ThingsPanel => root.GetProperty("sub_device_data").GetProperty("pump")[0].GetProperty("values"),
            _ => root
        };
        Assert.Equal(ulong.MaxValue, values.GetProperty("counter").GetUInt64());
        Assert.False(values.TryGetProperty("failed", out _));
        Assert.Equal("msg-1", values.GetProperty("_edge").GetProperty("messageId").GetString());
        Assert.Equal("Bad", values.GetProperty("_edge").GetProperty("tags").GetProperty("failed").GetProperty("quality").GetString());
    }

    public static IEnumerable<object[]> Requests()
    {
        yield return [MqttPlatform.Generic, "edge/edge-001/rpc/request", "{\"id\":7,\"device\":\"pump\",\"method\":\"write\",\"params\":{\"tag\":\"speed\",\"value\":12}}", "edge/edge-001/rpc/response"];
        yield return [MqttPlatform.ThingsBoard, "v1/gateway/rpc", "{\"device\":\"pump\",\"data\":{\"id\":7,\"method\":\"write\",\"params\":{\"tag\":\"speed\",\"value\":12}}}", "v1/gateway/rpc"];
        yield return [MqttPlatform.ThingsCloud, "gateway/command/send", "{\"device\":\"pump\",\"command\":{\"id\":7,\"method\":\"write\",\"params\":{\"tag\":\"speed\",\"value\":12}}}", "gateway/command/reply"];
        yield return [MqttPlatform.ThingsPanel, "gateway/command/gateway-number/7", "{\"sub_device_data\":{\"pump\":{\"method\":\"write\",\"params\":{\"tag\":\"speed\",\"value\":12}}}}", "gateway/command/response/7"];
        yield return [MqttPlatform.IoTSharp, "devices/pump/rpc/request/write/7", "{\"tag\":\"speed\",\"value\":12}", "devices/pump/rpc/response/write/7"];
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void PlatformRpcUsesDocumentedRequestAndResponseEnvelope(MqttPlatform platform, string topic, string payload, string replyTopic)
    {
        var config = new GatewayConfiguration { Mqtt = new() { Platform = platform, PlatformGatewayId = "gateway-number" } };
        var request = PlatformMqttAdapter.ParseRpc(config, new(topic, payload, DateTimeOffset.UtcNow));
        Assert.NotNull(request);
        Assert.Equal("pump", request.Device); Assert.Equal("speed", request.Tag); Assert.Equal("write", request.Method);
        Assert.Equal("7", request.Id); Assert.Equal(12, request.Value!.Value.GetInt32());
        var reply = PlatformMqttAdapter.RpcReply(request, new(true, "Matched"));
        Assert.Equal(replyTopic, reply.Topic);
        using var parsed = JsonDocument.Parse(reply.Payload);
        var root = parsed.RootElement;
        if (platform == MqttPlatform.ThingsBoard)
        { Assert.Equal(7, root.GetProperty("id").GetInt32()); Assert.True(root.GetProperty("data").GetProperty("success").GetBoolean()); }
        if (platform == MqttPlatform.ThingsCloud)
        { Assert.Equal(7, root.GetProperty("command").GetProperty("id").GetInt32()); Assert.True(root.GetProperty("command").GetProperty("params").GetProperty("success").GetBoolean()); }
        if (platform == MqttPlatform.ThingsPanel)
        { Assert.Equal(0, root.GetProperty("result").GetInt32()); Assert.Equal("Matched", root.GetProperty("message").GetString()); }
        if (platform == MqttPlatform.IoTSharp)
        { Assert.Equal("7", root.GetProperty("ResponseId").GetString()); using var data = JsonDocument.Parse(root.GetProperty("Data").GetString()!); Assert.True(data.RootElement.GetProperty("success").GetBoolean()); }
    }

    [Fact]
    public void EchoRetainedOversizeExpiredAndAmbiguousBatchDoNotBecomeCommands()
    {
        var config = new GatewayConfiguration { Mqtt = new() { Platform = MqttPlatform.ThingsBoard } };
        Assert.Null(PlatformMqttAdapter.ParseRpc(config, new("v1/gateway/rpc", "{\"device\":\"pump\",\"id\":1,\"data\":{\"success\":true}}", DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidDataException>(() => PlatformMqttAdapter.ParseRpc(config, new("v1/gateway/rpc", "{}", DateTimeOffset.UtcNow, true)));
        Assert.Throws<InvalidDataException>(() => PlatformMqttAdapter.ParseRpc(config, new("v1/gateway/rpc", new string('a', 65537), DateTimeOffset.UtcNow)));
        config.Mqtt.Platform = MqttPlatform.Generic;
        Assert.Throws<InvalidDataException>(() => PlatformMqttAdapter.ParseRpc(config, new("edge/edge-001/rpc/request",
            "{\"id\":1,\"device\":\"pump\",\"method\":\"write\",\"params\":{\"tag\":\"speed\",\"value\":12,\"expiresAt\":\"2020-01-01T00:00:00Z\"}}", DateTimeOffset.UtcNow)));
        config.Mqtt.Platform = MqttPlatform.ThingsPanel; config.Mqtt.PlatformGatewayId = "gw";
        Assert.Throws<InvalidDataException>(() => PlatformMqttAdapter.ParseRpc(config, new("gateway/command/gw/7", "{\"sub_device_data\":{\"a\":{},\"b\":{}}}", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void ValidationRejectsTopicAndIdentityConflictsWithoutDemandingRuntimeSecrets()
    {
        var config = new GatewayConfiguration { Mqtt = new() { Enabled = true, RpcEnabled = true, RpcRequestTopic = "edge/#" } };
        Assert.Contains(PlatformMqttAdapter.ValidateGateway(config), error => error.Contains("主题"));
        config.Mqtt.Platform = MqttPlatform.IoTSharp;
        config.Devices = [new() { Name = "a/b", Tags = [new() { Name = "_edge" }] }, new() { Name = "a/b" }];
        var errors = PlatformMqttAdapter.ValidateGateway(config);
        Assert.Contains(errors, error => error.Contains("设备名唯一"));
        Assert.Contains(errors, error => error.Contains("IoTSharp"));
        Assert.Contains(errors, error => error.Contains("保留名称"));
        Assert.DoesNotContain(errors, error => error.Contains("未设置"));
    }

    [Fact]
    public void GenericRpcTopicsAreDistinctAndBoundedAfterGatewayExpansion()
    {
        var config = new GatewayConfiguration
        { Mqtt = new() { Enabled = true, RpcEnabled = true, RpcResponseTopic = "edge/edge-001/rpc/request" } };
        Assert.Contains(PlatformMqttAdapter.ValidateGateway(config), error => error.Contains("展开") && error.Contains("相同"));
        config.Mqtt.RpcResponseTopic = "edge/{gatewayId}/rpc/response";
        config.Mqtt.Topic = "edge/edge-001/rpc/request";
        Assert.Contains(PlatformMqttAdapter.ValidateGateway(config), error => error.Contains("展开") && error.Contains("相同"));
        config.Mqtt.Topic = "edge/{gatewayId}/telemetry";
        Assert.Empty(PlatformMqttAdapter.ValidateGateway(config));
        config.GatewayId = new string('g', 100);
        config.Mqtt.RpcRequestTopic = new string('r', 3970) + "{gatewayId}";
        Assert.Contains(PlatformMqttAdapter.ValidateGateway(config), error => error.Contains("展开") && error.Contains("4000"));
    }
}
