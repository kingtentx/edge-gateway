using System.Text.Json;
using EdgeGateway.Model;
using MQTTnet;
using MQTTnet.Protocol;

namespace EdgeGateway.Service;

public sealed partial class MqttTelemetryPublisher
{
    private readonly HashSet<string> _cloudConnectedDevices = new(StringComparer.Ordinal);
    private volatile CloudPendingResponse? _pendingCloudResponse;
    private volatile MqttPlatform _activePlatform;

    public async Task PublishPlatformTelemetryAsync(MqttConfiguration settings, string topic, string payload,
        IReadOnlyCollection<string> currentlyOnlineDevices, CancellationToken cancellationToken)
    {
        if (settings.Platform != MqttPlatform.ThingsCloud || topic != "gateway/attributes")
        {
            await PublishAsync(settings, topic, payload, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!settings.Enabled) throw new InvalidOperationException("MQTT 上报未启用。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.TimeoutMs);
            await ConnectCoreAsync(settings, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("ThingsCloud 遥测载荷必须为设备对象映射。");
            var targets = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            var online = currentlyOnlineDevices.ToHashSet(StringComparer.Ordinal);
            foreach (var name in targets.Where(online.Contains))
                await EnsureCloudRegistrationAsync(settings, name, timeout.Token).ConfigureAwait(false);
            foreach (var name in targets.Where(name => !online.Contains(name)))
                await DisconnectCloudDeviceAsync(name, timeout.Token).ConfigureAwait(false);

            var temporaryRoutes = new List<string>();
            try
            {
                try { await PublishCloudAttributesAsync(payload, timeout.Token).ConfigureAwait(false); }
                catch (CloudReplyException exception) when (exception.Code == "421" && !string.IsNullOrWhiteSpace(settings.ThingsCloudTypeKey))
                {
                    // Durable history belongs to its original route even if that device was removed
                    // locally. Repair a missing cloud binding, send the immutable snapshot, then close
                    // temporary routes; do not suppress it based on the current local configuration.
                    foreach (var name in targets.Where(name => !online.Contains(name)))
                    {
                        await EnsureCloudRegistrationAsync(settings, name, timeout.Token).ConfigureAwait(false);
                        temporaryRoutes.Add(name);
                    }
                    await PublishCloudAttributesAsync(payload, timeout.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                foreach (var name in temporaryRoutes)
                {
                    await DisconnectCloudDeviceAsync(name, closeDeadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch { await DisconnectAfterFailureAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    private async Task PublishMessageCoreAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken)
    {
        if (settings.Platform == MqttPlatform.ThingsCloud && topic is "gateway/connect" or "gateway/disconnect")
        {
            using var document = JsonDocument.Parse(payload);
            var name = document.RootElement.GetProperty("device").GetString() ?? throw new InvalidDataException("缺少 ThingsCloud 子设备地址。");
            if (topic == "gateway/connect") await EnsureCloudRegistrationAsync(settings, name, cancellationToken).ConfigureAwait(false);
            else await DisconnectCloudDeviceAsync(name, cancellationToken).ConfigureAwait(false);
            return;
        }
        await PublishWireMessageAsync(topic, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishWireMessageAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build();
        // No reconnect here: a lost session must return failure to the caller, preserving its outbox row.
        var result = await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) throw new IOException($"MQTT 发布未确认：{result.ReasonCode}");
    }

    private async Task EnsureCloudRegistrationAsync(MqttConfiguration settings, string name, CancellationToken cancellationToken)
    {
        if (_cloudConnectedDevices.Contains(name)) return;
        var message = PlatformMqttAdapter.DevicePresence(settings, name, true)!;
        var pending = new CloudPendingResponse("gateway/connect/response", name);
        _pendingCloudResponse = pending;
        try
        {
            await PublishWireMessageAsync(message.Topic, message.Payload, cancellationToken).ConfigureAwait(false);
            await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _cloudConnectedDevices.Add(name);
        }
        finally { _pendingCloudResponse = null; }
    }

    private async Task PublishCloudAttributesAsync(string payload, CancellationToken cancellationToken)
    {
        var pending = new CloudPendingResponse("gateway/attributes/response", null);
        _pendingCloudResponse = pending;
        try
        {
            await PublishWireMessageAsync("gateway/attributes", payload, cancellationToken).ConfigureAwait(false);
            await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pendingCloudResponse = null; }
    }

    private async Task DisconnectCloudDeviceAsync(string name, CancellationToken cancellationToken)
    {
        if (!_cloudConnectedDevices.Contains(name)) return;
        await PublishWireMessageAsync("gateway/disconnect", JsonSerializer.Serialize(new { device = name }), cancellationToken).ConfigureAwait(false);
        _cloudConnectedDevices.Remove(name);
    }

    private bool TryHandlePlatformResponse(string topic, string payload)
    {
        if (_activePlatform != MqttPlatform.ThingsCloud || topic is not ("gateway/connect/response" or "gateway/attributes/response")) return false;
        var pending = _pendingCloudResponse;
        if (pending is null || pending.Topic != topic) return true;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.GetProperty("result").GetInt32() != 1)
            {
                var code = root.TryGetProperty("errcode", out var errorCode) ? errorCode.ToString() : "Rejected";
                var error = root.TryGetProperty("message", out var message) ? message.GetString() : "平台拒绝数据";
                pending.Completion.TrySetException(new CloudReplyException(code, $"ThingsCloud {topic} 拒绝（{code}）：{error}"));
            }
            else if (pending.Device is null || root.GetProperty("device").GetProperty("sub_device_addr").GetString() == pending.Device)
                pending.Completion.TrySetResult();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { pending.Completion.TrySetException(new InvalidDataException("ThingsCloud 响应格式无效。", exception)); }
        return true;
    }

    private sealed class CloudPendingResponse(string topic, string? device)
    {
        public string Topic { get; } = topic;
        public string? Device { get; } = device;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class CloudReplyException(string code, string message) : IOException(message)
    { public string Code { get; } = code; }
}
