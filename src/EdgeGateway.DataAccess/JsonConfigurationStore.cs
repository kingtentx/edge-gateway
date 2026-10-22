using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

/// <summary>Atomic replacement preserves the last valid configuration as .bak. Invalid input is never silently reset.</summary>
public sealed class JsonConfigurationStore(string path, Func<GatewayConfiguration>? defaultFactory = null) : IConfigurationStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<GatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return defaultFactory?.Invoke() ?? new GatewayConfiguration();
            try
            {
                await using var stream = File.OpenRead(_path);
                return await JsonSerializer.DeserializeAsync<GatewayConfiguration>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("配置文件内容为空。");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"配置文件格式错误，原文件未修改。请修复 {_path} 或检查对应的 .bak 备份。", ex);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(GatewayConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Serialize first: invalid numeric values or object graphs cannot damage the existing file.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration, SerializerOptions);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, _path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }
}
