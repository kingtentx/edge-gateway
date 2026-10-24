using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

/// <summary>Atomic replacement preserves the last valid configuration as .bak. Invalid input is never silently reset.</summary>
public sealed class JsonConfigurationStore(string path, Func<GatewayConfiguration>? defaultFactory = null) : IConfigurationStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static JsonSerializerOptions SerializerOptions => GatewayJson.Options;

    public async Task<GatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return defaultFactory?.Invoke() ?? new GatewayConfiguration();
            try
            {
                await using var stream = File.OpenRead(_path);
                var configuration = await JsonSerializer.DeserializeAsync<GatewayConfiguration>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("配置文件内容为空。");
                return UpgradeConfiguration(configuration);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"配置文件格式错误，原文件未修改。请修复 {_path} 或检查对应的 .bak 备份。", ex);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Apply additive v1→v2 migration in memory, also used by JSON import. Saving remains explicit.</summary>
    public static GatewayConfiguration UpgradeConfiguration(GatewayConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion == 1) configuration.SchemaVersion = 2;
        if (configuration.SchemaVersion != 2)
            throw new InvalidDataException($"不支持配置版本 {configuration.SchemaVersion}；仅支持版本 1（自动迁移）与 2，原文件未修改。");
        return configuration;
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
