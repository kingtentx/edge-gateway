using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed partial class GatewayRuntime
{
    private readonly object _rpcStatusSync = new();
    private RpcRuntimeStatus _rpcStatus = new();
    private string? _rpcConnectionError;
    private readonly Dictionary<string, (string Fingerprint, MqttOutboundMessage Reply)> _rpcReadCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _rpcReadCacheOrder = new();

    public RpcRuntimeStatus GetRpcStatus()
    {
        lock (_rpcStatusSync)
            return _rpcStatus with
            {
                Enabled = _configuration.Mqtt.Enabled && _configuration.Mqtt.RpcEnabled,
                AllowRemoteWrite = _configuration.Mqtt.AllowRemoteWrite,
                Subscribed = _running && _publisher.IsConnected && _rpcStatus.Subscribed,
                DroppedMessages = (_publisher as IMqttRpcTransport)?.DroppedMessages ?? 0,
                Error = string.Join("；", new[] { _rpcConnectionError, _rpcStatus.Error }.Where(error => !string.IsNullOrEmpty(error))) is { Length: > 0 } combined ? combined : null
            };
    }

    private Task PublishPlatformTelemetryAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var settings = _configuration.Mqtt;
        if (_publisher is IMqttPlatformTransport transport)
            return transport.PublishPlatformTelemetryAsync(settings, message.Topic, message.Payload,
                GetDeviceStatuses().Where(status => status.State == DeviceState.Running).Select(status => status.DeviceName).ToArray(), cancellationToken);
        if (settings.Platform == MqttPlatform.ThingsCloud)
            throw new InvalidOperationException("ThingsCloud 上报组件必须支持子设备注册和平台结果确认。");
        return _publisher.PublishAsync(settings, message.Topic, message.Payload, cancellationToken);
    }

    private async Task RunPlatformSessionAsync(CancellationToken cancellationToken)
    {
        if (_publisher is not IMqttRpcTransport transport)
        {
            if (_configuration.Mqtt.RpcEnabled)
                SetRpcError("当前上报组件未实现 MQTT 订阅，RPC 未启动。", connection: true);
            return;
        }
        await Task.WhenAll(MaintainPlatformSessionAsync(transport, cancellationToken), ReceiveRpcRequestsAsync(transport, cancellationToken)).ConfigureAwait(false);
    }

    private async Task MaintainPlatformSessionAsync(IMqttRpcTransport transport, CancellationToken cancellationToken)
    {
        long connection = -1;
        var announced = new HashSet<string>(StringComparer.Ordinal);
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var configuration = _configuration;
                    await transport.EnsureConnectedAsync(configuration.Mqtt, PlatformMqttAdapter.GetRpcSubscriptions(configuration), cancellationToken).ConfigureAwait(false);
                    lock (_rpcStatusSync) { _rpcConnectionError = null; _rpcStatus = _rpcStatus with { Subscribed = configuration.Mqtt.RpcEnabled }; }
                    failures = 0;
                    if (connection != transport.ConnectionVersion)
                    {
                        announced.Clear();
                        connection = transport.ConnectionVersion;
                    }
                    var connectedNames = GetDeviceStatuses().Where(status => status.State == DeviceState.Running)
                        .Select(status => status.DeviceName).ToHashSet(StringComparer.Ordinal);
                    foreach (var name in announced.Except(connectedNames).ToArray())
                    {
                        var message = PlatformMqttAdapter.DevicePresence(configuration.Mqtt, name, false);
                        if (message is not null) await _publisher.PublishAsync(configuration.Mqtt, message.Topic, message.Payload, cancellationToken).ConfigureAwait(false);
                        announced.Remove(name);
                    }
                    foreach (var name in connectedNames.Except(announced).ToArray())
                    {
                        var message = PlatformMqttAdapter.DevicePresence(configuration.Mqtt, name, true);
                        if (message is not null) await _publisher.PublishAsync(configuration.Mqtt, message.Topic, message.Payload, cancellationToken).ConfigureAwait(false);
                        announced.Add(name);
                    }
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    failures = Math.Min(32, failures + 1);
                    lock (_rpcStatusSync) _rpcStatus = _rpcStatus with { Subscribed = false };
                    SetRpcError($"平台连接或 RPC 订阅未完成：{RedactRpc(exception.Message)}", connection: true);
                    await Task.Delay(Backoff(Math.Min(failures, 32)), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (_rpcStatusSync) _rpcStatus = _rpcStatus with { Subscribed = false };
            // One deadline covers every best-effort offline notice, so shutdown cannot grow with device count.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var name in announced)
            {
                var message = PlatformMqttAdapter.DevicePresence(_configuration.Mqtt, name, false);
                if (message is null) continue;
                try { await _publisher.PublishAsync(_configuration.Mqtt, message.Topic, message.Payload, deadline.Token).ConfigureAwait(false); }
                catch { break; }
            }
        }
    }

    private async Task ReceiveRpcRequestsAsync(IMqttRpcTransport transport, CancellationToken cancellationToken)
    {
        var rateWindow = DateTimeOffset.UtcNow;
        var requestsInWindow = 0;
        try
        {
            while (true)
            {
                var message = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var configuration = _configuration;
                if (!configuration.Mqtt.RpcEnabled || !configuration.Mqtt.Enabled) continue;
                PlatformRpcRequest? request;
                try { request = PlatformMqttAdapter.ParseRpc(configuration, message); }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    lock (_rpcStatusSync) _rpcStatus = _rpcStatus with { RejectedRequests = _rpcStatus.RejectedRequests + 1 };
                    SetRpcError($"RPC 报文校验失败：{RedactRpc(exception.Message)}");
                    continue;
                }
                if (request is null) continue;
                var now = DateTimeOffset.UtcNow;
                if (now - rateWindow >= TimeSpan.FromSeconds(1)) { rateWindow = now; requestsInWindow = 0; }
                requestsInWindow++;
                lock (_rpcStatusSync)
                    _rpcStatus = _rpcStatus with { ReceivedRequests = _rpcStatus.ReceivedRequests + 1, LastRequestAt = now, LastRequestId = request.Id };
                MqttOutboundMessage reply;
                if (requestsInWindow > 20)
                    reply = RejectedRpcReply(request, "RateLimited", "RPC 超过每秒 20 条处理上限。");
                else if (now - request.ReceivedAt > TimeSpan.FromMilliseconds(configuration.Mqtt.RpcTimeoutMs))
                    reply = RejectedRpcReply(request, "Expired", "RPC 排队已超时，未执行设备操作。");
                else
                    reply = await ExecuteRpcRequestAsync(configuration, request, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _publisher.PublishAsync(configuration.Mqtt, reply.Topic, reply.Payload, cancellationToken).ConfigureAwait(false);
                    lock (_rpcStatusSync) _rpcStatus = _rpcStatus with { CompletedRequests = _rpcStatus.CompletedRequests + 1 };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    SetRpcError($"RPC 结果发送未获 Broker 确认；相同写请求可重放已保存结果：{RedactRpc(exception.Message)}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { SetRpcError($"RPC 接收工作异常：{RedactRpc(exception.Message)}"); }
    }

    private async Task<MqttOutboundMessage> ExecuteRpcRequestAsync(GatewayConfiguration configuration, PlatformRpcRequest request, CancellationToken cancellationToken)
    {
        var source = $"rpc:{request.Platform}:{request.Id}";
        try
        {
            if (request.Method is not ("read" or "write"))
                return RejectedRpcReply(request, "MethodNotAllowed", "只允许 read 和 write 点位命令。");
            var hasDeviceId = Guid.TryParse(request.Device, out var requestedDeviceId);
            var devices = configuration.Devices.Where(device => (hasDeviceId && device.Id == requestedDeviceId) || device.Name == request.Device).ToArray();
            if (devices.Length != 1) return RejectedRpcReply(request, "UnknownDevice", "设备不存在或名称不唯一。");
            var device = devices[0];
            var hasTagId = Guid.TryParse(request.Tag, out var requestedTagId);
            var tags = device.Tags.Where(tag => (hasTagId && tag.Id == requestedTagId) || tag.Name == request.Tag).ToArray();
            if (tags.Length != 1) return RejectedRpcReply(request, "UnknownTag", "点位不存在或名称不唯一。");
            var tag = tags[0];
            if (!device.Enabled || !tag.Enabled)
                return RejectedRpcReply(request, "TargetDisabled", "RPC 只允许操作启用设备及启用点位。");
            if (!IsDeviceRunning(device.Id))
                return RejectedRpcReply(request, "DeviceNotRunning", "RPC 只允许操作正在运行的设备。");
            if (request.Method == "write" && (!configuration.Mqtt.AllowRemoteWrite || !tag.Writable))
                return RejectedRpcReply(request, "WriteDenied", "远程写入未获允许，或目标点位只读。");
            // A token can rotate without creating a new logical command realm. Scope by its
            // configured variable name, never by resolved credentials, and include cloud identity.
            var account = string.IsNullOrWhiteSpace(configuration.Mqtt.UsernameEnvironmentVariable)
                ? $"username:{configuration.Mqtt.Username}" : $"environment:{configuration.Mqtt.UsernameEnvironmentVariable}";
            var key = Hash(JsonSerializer.Serialize(new[]
            {
                request.Platform.ToString(), configuration.Mqtt.Host, configuration.Mqtt.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                configuration.Mqtt.ClientId, configuration.GatewayId, configuration.Mqtt.PlatformGatewayId, account, device.Id.ToString(), request.Id
            }));
            var fingerprint = Hash($"{request.Method}\n{tag.Id}\n{request.Value?.GetRawText() ?? ""}");
            if (_rpcReadCache.TryGetValue(key, out var cached))
            {
                if (cached.Fingerprint != fingerprint) return RejectedRpcReply(request, "RequestIdConflict", "此请求标识已用于不同的命令。");
                lock (_rpcStatusSync) _rpcStatus = _rpcStatus with { ReplayedRequests = _rpcStatus.ReplayedRequests + 1, LastResult = "Replayed" };
                return cached.Reply;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(configuration.Mqtt.RpcTimeoutMs);
            if (request.Method == "read" && _store is IRpcCommandJournal writeJournal &&
                await writeJournal.GetRpcCommandAsync(key, timeout.Token).ConfigureAwait(false) is not null)
                return RejectedRpcReply(request, "RequestIdConflict", "此请求标识已用于写命令，禁止改用读取方法。");
            _log.Write(GatewayLogLevel.Information, $"RPC [{request.Id}] {request.Method}：设备 [{device.Name}] 点位 [{tag.Name}]。");
            MqttOutboundMessage reply;
            if (request.Method == "read")
            {
                var result = await ProbeDeviceAsync(device.Id, tag.Id, new DeviceProbeOptions
                { RequireRunning = true, Source = source, TimeoutMs = configuration.Mqtt.RpcTimeoutMs }, timeout.Token).ConfigureAwait(false);
                var state = result.State == DeviceProbeState.Cancelled && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? "TimedOut" : result.State.ToString();
                var response = new RpcCommandResponse(result.Success, state, RedactRpcNullable(result.Error), new
                { deviceId = device.Id, tagId = tag.Id, sample = result.Sample, elapsedMs = result.Elapsed.TotalMilliseconds }, result.OperationId);
                reply = PlatformMqttAdapter.RpcReply(request, response);
                if (_rpcReadCache.Count >= 1024) _rpcReadCache.Remove(_rpcReadCacheOrder.Dequeue());
                _rpcReadCache.Add(key, (fingerprint, reply));
                _rpcReadCacheOrder.Enqueue(key);
                lock (_rpcStatusSync) _rpcStatus = _rpcStatus with { LastResult = response.status, Error = response.success ? null : response.error };
            }
            else
            {
                if (_store is not IRpcCommandJournal journal)
                    return RejectedRpcReply(request, "JournalUnavailable", "存储未提供持久命令审计，拒绝远程写入。");
                var value = RpcValue(request.Value);
                var claim = await journal.ClaimRpcCommandAsync(key, fingerprint, 10000, timeout.Token).ConfigureAwait(false);
                if (!claim.Created)
                {
                    if (claim.Entry.Fingerprint != fingerprint) return RejectedRpcReply(request, "RequestIdConflict", "此请求标识已用于不同的写命令。");
                    if (claim.Entry.ResponseTopic is null || claim.Entry.ResponsePayload is null)
                        return RejectedRpcReply(request, "Indeterminate", "此写命令已有执行记录，但最终结果未持久化；必须现场核对，禁止自动重复写入。");
                    lock (_rpcStatusSync) _rpcStatus = _rpcStatus with { ReplayedRequests = _rpcStatus.ReplayedRequests + 1, LastResult = "Replayed" };
                    return new(claim.Entry.ResponseTopic, claim.Entry.ResponsePayload);
                }
                RpcCommandResponse response;
                try
                {
                    var commandId = new Guid(Convert.FromHexString(key)[..16]);
                    var result = await WriteVerifiedAsync(device.Id, tag.Id, value, new WriteVerificationOptions
                    {
                        CommandId = commandId, Source = source, VerifyReadBack = true, RequireRemoteWritePermission = true,
                        TimeoutMs = configuration.Mqtt.RpcTimeoutMs, MaxAttempts = 5, RetryIntervalMs = 200
                    }, timeout.Token).ConfigureAwait(false);
                    var state = result.State == WriteCommandState.Cancelled && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                        ? "TimedOut" : result.State.ToString();
                    response = new(result.State == WriteCommandState.Matched, state, RedactRpcNullable(result.Error), new
                    {
                        deviceId = device.Id, tagId = tag.Id, submitted = result.Submitted,
                        readBack = result.ReadBack, attempts = result.ReadBackAttempts, elapsedMs = result.Elapsed.TotalMilliseconds
                    }, result.CommandId);
                }
                catch (OperationCanceledException)
                { response = new(false, "Cancelled", "写入调用已取消；设备可能已接收命令，请核对现场状态。"); }
                catch (Exception exception)
                { response = new(false, "Failed", RedactRpc(exception.Message)); }
                reply = PlatformMqttAdapter.RpcReply(request, response);
                // Give journal admission a separate shutdown cancellation budget. An interrupted
                // completion leaves Pending, which prevents redispatch on the next process start.
                using var journalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await journal.CompleteRpcCommandAsync(key, reply.Topic, reply.Payload, journalDeadline.Token).ConfigureAwait(false);
                lock (_rpcStatusSync) _rpcStatus = _rpcStatus with { LastResult = response.status, Error = response.success ? null : response.error };
                _log.Write(response.success ? GatewayLogLevel.Information : GatewayLogLevel.Warning,
                    $"RPC 写命令 [{request.Id}] 结果 {response.status}，已持久记录。{response.error}");
            }
            return reply;
        }
        catch (OperationCanceledException)
        { return RejectedRpcReply(request, cancellationToken.IsCancellationRequested ? "Cancelled" : "TimedOut", "RPC 操作超时或已取消。"); }
        catch (Exception exception) { return RejectedRpcReply(request, "Failed", RedactRpc(exception.Message)); }
    }

    private MqttOutboundMessage RejectedRpcReply(PlatformRpcRequest request, string status, string error)
    {
        lock (_rpcStatusSync)
            _rpcStatus = _rpcStatus with { RejectedRequests = _rpcStatus.RejectedRequests + 1, LastResult = status, Error = error };
        _log.Write(GatewayLogLevel.Warning, $"RPC [{request.Id}] {status}：{error}");
        return PlatformMqttAdapter.RpcReply(request, new(false, status, error));
    }

    private void SetRpcError(string error, bool connection = false)
    {
        bool changed;
        lock (_rpcStatusSync)
        {
            if (connection) { changed = _rpcConnectionError != error; _rpcConnectionError = error; }
            else { changed = _rpcStatus.Error != error; _rpcStatus = _rpcStatus with { Error = error }; }
        }
        if (changed) _log.Write(GatewayLogLevel.Warning, error);
    }

    private static string RpcValue(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString()!,
        { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => throw new InvalidDataException("RPC 写入 value 必须为字符串、数字或布尔值，不能为 null、数组或对象。")
    };
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string RedactRpc(string text)
    {
        text = Redact(text);
        var name = _configuration.Mqtt.UsernameEnvironmentVariable;
        var token = string.IsNullOrEmpty(name) ? null : Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(token) ? text : text.Replace(token, "***", StringComparison.Ordinal);
    }
    private string? RedactRpcNullable(string? text) => text is null ? null : RedactRpc(text);
}
