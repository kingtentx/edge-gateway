using EdgeGateway.Model;

namespace EdgeGateway.Abstractions;

/// <summary>Import/export files selected by a caller; separate from the active configuration store.</summary>
public interface IConfigurationFiles
{
    Task<GatewayConfiguration> ReadAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, GatewayConfiguration configuration, CancellationToken cancellationToken = default);
}

public static class MqttTransportLimits
{
    public const int MaximumRpcPayloadBytes = 65536;
}
