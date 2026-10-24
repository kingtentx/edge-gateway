using EdgeGateway.Model;

namespace EdgeGateway.Abstractions;

/// <summary>One instance per device. Callers serialize reads and writes. Implementations must honor cancellation.</summary>
public interface IDeviceDriver : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken);
    Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken);
}

public interface IDriverFactory
{
    DriverDescriptor Descriptor { get; }
    IDeviceDriver Create(DeviceConfiguration configuration);
    IReadOnlyList<string> Validate(DeviceConfiguration configuration);
}

public interface IDriverRegistry
{
    IReadOnlyList<DriverDescriptor> Drivers { get; }
    IDriverFactory GetFactory(string driverId);
}

public interface IGatewayLog
{
    void Write(GatewayLogLevel level, string message);
    IReadOnlyList<GatewayLogEntry> GetEntries(int limit = 500);
}

public interface IConfigurationStore
{
    Task<GatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(GatewayConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface IGatewayStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    // Never silently evict unacknowledged messages. Return false if capacity is exhausted.
    Task<bool> EnqueueAsync(string topic, string payload, int maxMessages, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(int limit, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(long id, CancellationToken cancellationToken = default);
    Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

public interface ITelemetryPublisher : IAsyncDisposable
{
    bool IsConnected { get; }
    Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
