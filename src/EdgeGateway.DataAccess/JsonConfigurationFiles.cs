using System.Text;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

public sealed class JsonConfigurationFiles : IConfigurationFiles
{
    public Task<GatewayConfiguration> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("所选配置文件不存在。", path);
        return new JsonConfigurationStore(path).LoadAsync(cancellationToken);
    }

    public Task ExportAsync(string path, GatewayConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(configuration);
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration, GatewayJson.Options), new UTF8Encoding(false), cancellationToken);
    }
}
