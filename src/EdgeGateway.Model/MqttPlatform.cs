namespace EdgeGateway.Model;

public enum MqttPlatform { Generic, ThingsBoard, ThingsCloud, ThingsPanel, IoTSharp }

public sealed record MqttOutboundMessage(string Topic, string Payload);
public sealed record MqttInboundMessage(string Topic, string Payload, DateTimeOffset ReceivedAt, bool Retained = false);

public sealed record RpcRuntimeStatus
{
    public bool Enabled { get; init; }
    public bool AllowRemoteWrite { get; init; }
    public bool Subscribed { get; init; }
    public long ReceivedRequests { get; init; }
    public long CompletedRequests { get; init; }
    public long RejectedRequests { get; init; }
    public long ReplayedRequests { get; init; }
    public long DroppedMessages { get; init; }
    public DateTimeOffset? LastRequestAt { get; init; }
    public string? LastRequestId { get; init; }
    public string? LastResult { get; init; }
    public string? Error { get; init; }
}

/// <summary>A pending durable claim is never replayed as a new write after a crash.</summary>
public sealed record RpcJournalEntry(string Key, string Fingerprint, string? ResponseTopic,
    string? ResponsePayload, DateTimeOffset CreatedAt);
public sealed record RpcJournalClaim(bool Created, RpcJournalEntry Entry);
