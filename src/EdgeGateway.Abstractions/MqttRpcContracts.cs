using EdgeGateway.Model;

namespace EdgeGateway.Abstractions;

/// <summary>Optional duplex transport. Incoming work is bounded and never runs on the MQTT callback.</summary>
public interface IMqttRpcTransport
{
    long ConnectionVersion { get; }
    long DroppedMessages { get; }
    Task EnsureConnectedAsync(MqttConfiguration settings, IReadOnlyList<string> subscriptionTopics, CancellationToken cancellationToken);
    ValueTask<MqttInboundMessage> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Durable at-most-once dispatch for remote writes; an indeterminate claim must not execute again.</summary>
public interface IRpcCommandJournal
{
    Task<RpcJournalEntry?> GetRpcCommandAsync(string key, CancellationToken cancellationToken = default);
    Task<RpcJournalClaim> ClaimRpcCommandAsync(string key, string fingerprint, int maxEntries, CancellationToken cancellationToken = default);
    Task CompleteRpcCommandAsync(string key, string responseTopic, string responsePayload, CancellationToken cancellationToken = default);
}

/// <summary>Serializes platform registration and telemetry on the same MQTT connection.</summary>
public interface IMqttPlatformTransport
{
    Task PublishPlatformTelemetryAsync(MqttConfiguration settings, string topic, string payload,
        IReadOnlyCollection<string> currentlyOnlineDevices, CancellationToken cancellationToken);
}
