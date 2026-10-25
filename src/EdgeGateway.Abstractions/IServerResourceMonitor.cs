using EdgeGateway.Model;

namespace EdgeGateway.Abstractions;

public interface IServerResourceMonitor
{
    Task<ServerResourceSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}
