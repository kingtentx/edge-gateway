using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed partial class GatewayRuntime
{
    private readonly object _telemetryStatusSync = new();
    private TelemetryPipelineStatus _telemetryStatus = new();
    private string? _telemetryEnqueueError;
    private string? _telemetryOutboxReadError;
    private string? _telemetryOutboxAcknowledgementError;
    private TelemetryDeliveryStage? _telemetryLastFailureStage;

    public TelemetryPipelineStatus GetTelemetryStatus()
    {
        var configuration = _configuration;
        lock (_telemetryStatusSync)
        {
            var errors = new[] { _telemetryEnqueueError, _telemetryOutboxReadError, _telemetryOutboxAcknowledgementError }
                .Where(error => error is not null).Distinct().ToArray();
            return _telemetryStatus with
            {
                Enabled = configuration.Mqtt.Enabled,
                Running = _running && configuration.Mqtt.Enabled,
                PersistenceError = errors.Length == 0 ? null : string.Join("；", errors)
            };
        }
    }

    private async Task QueueTelemetryAsync(CancellationToken cancellationToken)
    {
        var planner = new TelemetryReportPlanner();
        GatewayConfiguration? plannedConfiguration = null;
        var generations = new Dictionary<Guid, long>();
        try
        {
            while (true)
            {
                await Task.Delay(_configuration.PublishIntervalMs, cancellationToken).ConfigureAwait(false);
                try
                {
                    var configuration = _configuration;
                    if (!configuration.Mqtt.Enabled) continue;
                    if (!ReferenceEquals(configuration, plannedConfiguration))
                    {
                        // A new runtime/configuration starts with a complete eligible snapshot.
                        planner.Reset();
                        plannedConfiguration = configuration;
                    }
                    foreach (var device in configuration.Devices)
                    {
                        var generation = _deviceGenerations.GetValueOrDefault(device.Id);
                        if (!generations.TryGetValue(device.Id, out var previous) || generation != previous)
                        {
                            planner.Forget(device.Tags.Select(tag => tag.Id));
                            generations[device.Id] = generation;
                        }
                    }
                    var now = DateTimeOffset.UtcNow;
                    var samples = planner.Select(configuration, GetLatest(), now);
                    if (samples.Count == 0) continue;
                    var messages = PlatformMqttAdapter.BuildTelemetry(configuration, samples, Guid.NewGuid().ToString("N"), now);
                    if (messages.Count == 0) throw new InvalidDataException("平台适配器未为有效快照生成消息。");
                    var superseded = false;
                    foreach (var message in messages)
                    {
                        // Retry this exact message/id until durable. Previous platform fragments
                        // remain durable if configuration changes halfway through the snapshot.
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!ReferenceEquals(configuration, _configuration))
                            {
                                superseded = true;
                                lock (_telemetryStatusSync)
                                    _telemetryStatus = _telemetryStatus with { QueueCapacityBlocked = false };
                                break;
                            }
                            try
                            {
                                lock (_telemetryStatusSync)
                                    _telemetryStatus = _telemetryStatus with { LastEnqueueAttemptAt = DateTimeOffset.UtcNow };
                                if (await _store.EnqueueAsync(message.Topic, message.Payload, configuration.MaxOutboxMessages, cancellationToken).ConfigureAwait(false))
                                {
                                    bool recovered;
                                    lock (_telemetryStatusSync)
                                    {
                                        recovered = _telemetryStatus.QueueCapacityBlocked || _telemetryEnqueueError is not null;
                                        _telemetryEnqueueError = null;
                                        _telemetryStatus = _telemetryStatus with
                                        { LastEnqueuedAt = DateTimeOffset.UtcNow, QueueCapacityBlocked = false };
                                    }
                                    if (recovered) _log.Write(GatewayLogLevel.Information, "上报快照已恢复持久化，继续生成待发送消息。");
                                    break;
                                }
                                bool firstCapacityFailure;
                                lock (_telemetryStatusSync)
                                {
                                    firstCapacityFailure = !_telemetryStatus.QueueCapacityBlocked;
                                    _telemetryEnqueueError = null;
                                    _telemetryStatus = _telemetryStatus with
                                    { QueueCapacityBlocked = true, LastCapacityBlockedAt = DateTimeOffset.UtcNow };
                                }
                                if (firstCapacityFailure)
                                    _log.Write(GatewayLogLevel.Error, "补传队列已满，暂停生成新的上报快照；已入队数据保留，现场采集及历史记录继续。");
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exception)
                            {
                                RecordTelemetryEnqueueFailure("上报快照无法持久化，将重试", exception);
                            }
                            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                        }
                        if (superseded) break;
                    }
                    // Failed/full queues never advance deadband or heartbeat baselines.
                    if (!superseded && ReferenceEquals(configuration, _configuration))
                        planner.Commit(samples, DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    RecordTelemetryEnqueueFailure("生成上报快照失败，将重试", exception);
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (_telemetryStatusSync)
                _telemetryStatus = _telemetryStatus with { QueueCapacityBlocked = false };
        }
    }

    private void RecordTelemetryEnqueueFailure(string context, Exception exception)
    {
        var error = $"{context}（{exception.GetType().Name}）：{Redact(exception.Message)}";
        bool changed;
        lock (_telemetryStatusSync)
        {
            changed = _telemetryEnqueueError != error;
            _telemetryEnqueueError = error;
            _telemetryStatus = _telemetryStatus with
            { LastPersistenceFailureAt = DateTimeOffset.UtcNow, QueueCapacityBlocked = false };
        }
        if (changed) _log.Write(GatewayLogLevel.Error, error);
    }

    private async Task SendOutboxAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stage = TelemetryDeliveryStage.ReadOutbox;
                try
                {
                    lock (_telemetryStatusSync)
                        _telemetryStatus = _telemetryStatus with { NextRetryAt = null };
                    var messages = await _store.ReadOutboxAsync(50, cancellationToken).ConfigureAwait(false);
                    lock (_telemetryStatusSync)
                    {
                        // Clear only the storage operation that actually recovered. A successful
                        // read does not resolve an earlier failure to remove an acknowledged row.
                        if (_telemetryOutboxReadError is not null)
                        {
                            _telemetryOutboxReadError = null;
                            if (_telemetryLastFailureStage == TelemetryDeliveryStage.ReadOutbox)
                            {
                                _telemetryLastFailureStage = null;
                                _telemetryStatus = _telemetryStatus with { SendError = null };
                                if (messages.Count == 0 && _telemetryOutboxAcknowledgementError is null)
                                {
                                    failures = 0;
                                    _telemetryStatus = _telemetryStatus with { ConsecutiveFailures = 0 };
                                }
                            }
                        }
                    }
                    if (messages.Count == 0)
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    foreach (var message in messages)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stage = TelemetryDeliveryStage.Publish;
                        lock (_telemetryStatusSync)
                            _telemetryStatus = _telemetryStatus with
                            { Sending = true, LastSendAttemptAt = DateTimeOffset.UtcNow, NextRetryAt = null };
                        await PublishPlatformTelemetryAsync(message, cancellationToken).ConfigureAwait(false);

                        // The platform-specific delivery contract has completed: PUBACK,
                        // plus an application response where required (ThingsCloud).
                        // A subsequent local removal failure retains the row for redelivery.
                        lock (_telemetryStatusSync)
                            _telemetryStatus = _telemetryStatus with
                            {
                                Sending = false,
                                LastBrokerAcknowledgedAt = DateTimeOffset.UtcNow,
                                AcknowledgedMessages = _telemetryStatus.AcknowledgedMessages + 1
                            };
                        stage = TelemetryDeliveryStage.AcknowledgeOutbox;
                        await _store.AcknowledgeAsync(message.Id, cancellationToken).ConfigureAwait(false);
                        var recovered = failures > 0;
                        failures = 0;
                        lock (_telemetryStatusSync)
                        {
                            _telemetryOutboxAcknowledgementError = null;
                            _telemetryLastFailureStage = null;
                            _telemetryStatus = _telemetryStatus with
                            { SendError = null, ConsecutiveFailures = 0, NextRetryAt = null };
                        }
                        if (recovered) _log.Write(GatewayLogLevel.Information, "上报发送与队列确认已恢复，继续补传待发送消息。");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    failures = failures == int.MaxValue ? failures : failures + 1;
                    var delay = Backoff(failures);
                    var now = DateTimeOffset.UtcNow;
                    var context = stage switch
                    {
                        TelemetryDeliveryStage.ReadOutbox => "读取补传队列失败，稍后重试",
                        TelemetryDeliveryStage.AcknowledgeOutbox => "发送已确认，但本地队列确认失败；消息保留，重试可能重复发送",
                        _ => "MQTT 发送未获确认，消息保留待补传"
                    };
                    var error = $"{context}（{exception.GetType().Name}）：{Redact(exception.Message)}";
                    lock (_telemetryStatusSync)
                    {
                        _telemetryLastFailureStage = stage;
                        _telemetryStatus = _telemetryStatus with
                        {
                            Sending = false, SendError = error, LastSendFailureAt = now,
                            ConsecutiveFailures = failures, NextRetryAt = now.AddMilliseconds(delay)
                        };
                        if (stage != TelemetryDeliveryStage.Publish)
                        {
                            if (stage == TelemetryDeliveryStage.ReadOutbox) _telemetryOutboxReadError = error;
                            else _telemetryOutboxAcknowledgementError = error;
                            _telemetryStatus = _telemetryStatus with { LastPersistenceFailureAt = now };
                        }
                    }
                    _log.Write(GatewayLogLevel.Warning, error);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (_telemetryStatusSync)
                _telemetryStatus = _telemetryStatus with { Sending = false, NextRetryAt = null };
        }
    }

    private string Redact(string text)
    {
        foreach (var name in new[] { _configuration.Mqtt.PasswordEnvironmentVariable, _configuration.Mqtt.UsernameEnvironmentVariable })
        {
            var value = string.IsNullOrEmpty(name) ? null : Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) text = text.Replace(value, "***", StringComparison.Ordinal);
        }
        return text;
    }

    private enum TelemetryDeliveryStage { ReadOutbox, Publish, AcknowledgeOutbox }
}
