using System.Text;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

public sealed class JsonConfigurationFiles : IConfigurationFiles
{
    public async Task<GatewayConfiguration> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("所选配置文件不存在。", path);
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ConfigurationJson.Deserialize(document.RootElement);
        }
        catch (JsonException exception) { throw new InvalidDataException("配置文件格式错误，原文件未修改。", exception); }
    }

    public Task ExportAsync(string path, GatewayConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion != 3) throw new InvalidDataException("仅支持导出配置版本 3。");
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration, GatewayJson.Options), new UTF8Encoding(false), cancellationToken);
    }
}
