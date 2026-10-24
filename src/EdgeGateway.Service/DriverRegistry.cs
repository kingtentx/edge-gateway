using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Loader;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>
/// Thread-safe driver registry. Uploaded plugins are loaded from versioned directories and
/// share the host's contract/model assemblies. Plugins are trusted executable code, not sandboxed.
/// </summary>
public sealed class DriverRegistry : IDriverRegistry, IDriverPluginManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IDriverFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AssemblyLoadContext> _contexts = [];
    private readonly Dictionary<string, DriverPluginInfo> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly IGatewayLog _log;
    private string? _pluginDirectory;

    public DriverRegistry(IEnumerable<IDriverFactory> factories, IGatewayLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        foreach (var factory in factories) Register(factory);
    }

    public IReadOnlyList<DriverDescriptor> Drivers
    {
        get { lock (_sync) return _factories.Values.Select(f => f.Descriptor).OrderBy(d => d.Id).ToArray(); }
    }

    public IDriverFactory GetFactory(string driverId)
    {
        lock (_sync) return _factories.TryGetValue(driverId, out var factory) ? factory :
            throw new InvalidOperationException($"未安装驱动：{driverId}");
    }

    public IReadOnlyList<DriverPluginInfo> GetInstalledPlugins()
    {
        lock (_sync) return _plugins.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Loads existing plugins before runtime startup; invalid plugins do not prevent other drivers from loading.</summary>
    public void LoadPlugins(string directory)
    {
        _pluginDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(_pluginDirectory)) Directory.CreateDirectory(_pluginDirectory);

        // Backward-compatible discovery of trusted, directly deployed EdgeGateway.Driver.* DLLs.
        // Installed uploads are manifest-driven so dependency DLLs are never mistaken for plugins.
        foreach (var file in Directory.EnumerateFiles(_pluginDirectory, "EdgeGateway.Driver.*.dll", SearchOption.TopDirectoryOnly))
            TryLoadPlugin(file, null);

        var installed = Path.Combine(_pluginDirectory, "installed");
        if (!Directory.Exists(installed)) return;
        foreach (var manifestPath in Directory.EnumerateFiles(installed, "driver-plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath))
                    ?? throw new InvalidDataException("驱动清单为空。");
                var directoryPath = Path.GetDirectoryName(manifestPath)!;
                var assemblyPath = Path.GetFullPath(Path.Combine(directoryPath, manifest.AssemblyFile));
                if (!assemblyPath.StartsWith(Path.GetFullPath(directoryPath) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || !File.Exists(assemblyPath))
                    throw new InvalidDataException("驱动清单引用的 DLL 路径无效。");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath)));
                if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("驱动文件 SHA-256 与安装清单不一致。");
                TryLoadPlugin(assemblyPath, manifest);
            }
            catch (Exception ex)
            {
                _log.Write(GatewayLogLevel.Error, $"驱动插件清单 {Path.GetFileName(manifestPath)} 加载失败：{SafeMessage(ex)}");
            }
        }
    }

    /// <summary>
    /// Installs one DLL and registers its factory immediately. A plugin must implement IDriverFactory
    /// and must use the host's EdgeGateway.Abstractions/Model contracts. The DLL is not sandboxed.
    /// </summary>
    public string InstallPlugin(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (_pluginDirectory is null) throw new InvalidOperationException("驱动插件目录尚未初始化。");
        var source = Path.GetFullPath(sourcePath);
        var info = new FileInfo(source);
        if (!info.Exists || !string.Equals(info.Extension, ".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择有效的 .dll 驱动文件。", nameof(sourcePath));
        if (info.Length is < 1 or > 100L * 1024 * 1024)
            throw new ArgumentException("驱动 DLL 必须大于 0 字节且不超过 100 MiB。", nameof(sourcePath));

        var assemblyName = AssemblyName.GetAssemblyName(source);
        var simpleName = assemblyName.Name ?? throw new BadImageFormatException("无法读取 DLL 程序集名称。");
        if (simpleName.Length > 100 || simpleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new BadImageFormatException("DLL 程序集名称不合法。");
        var version = assemblyName.Version?.ToString() ?? "0.0.0.0";
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var installedRoot = Path.Combine(_pluginDirectory, "installed", simpleName, version);
        Directory.CreateDirectory(installedRoot);
        var destination = Path.Combine(installedRoot, simpleName + ".dll");
        if (File.Exists(destination))
        {
            var existingHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)));
            if (!string.Equals(existingHash, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"驱动 {simpleName} {version} 已安装但内容不同。请使用递增的程序集版本号发布新 DLL。");
        }
        else File.Copy(source, destination, overwrite: false);

        PluginManifest? manifest = null;
        try
        {
            var context = CreateContext(destination);
            var assembly = context.LoadFromAssemblyPath(destination);
            var factories = DiscoverFactories(assembly);
            if (factories.Count != 1)
                throw new InvalidDataException($"一个驱动 DLL 必须且只能公开一个 IDriverFactory；当前找到 {factories.Count} 个。");
            var factory = CreateFactory(factories[0]);
            ValidateDescriptor(factory.Descriptor);
            lock (_sync)
            {
                if (_factories.ContainsKey(factory.Descriptor.Id))
                    throw new InvalidOperationException($"驱动标识“{factory.Descriptor.Id}”已注册。为避免影响正在运行的设备，本版本不允许覆盖同 ID 驱动；请使用唯一驱动 ID。");
                _factories.Add(factory.Descriptor.Id, factory);
                _contexts.Add(context);
                manifest = new(simpleName + ".dll", sha256, factory.Descriptor.Id, factory.Descriptor.Name, version);
                _plugins[factory.Descriptor.Id] = new(factory.Descriptor.Id, factory.Descriptor.Name, version, simpleName, sha256, "已加载");
            }
            File.WriteAllText(Path.Combine(installedRoot, "driver-plugin.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            _log.Write(GatewayLogLevel.Information, $"驱动插件已安装并热加载：{factory.Descriptor.Name} ({factory.Descriptor.Id})，版本 {version}，SHA-256 {sha256}");
            return $"驱动 {factory.Descriptor.Name} ({factory.Descriptor.Id}) 版本 {version} 已安装并立即注册。无需重启网关；新建该类型设备前请先完成驱动参数与通信验证。";
        }
        catch
        {
            // The directory is intentionally retained if the assembly may already have been loaded;
            // an invalid upload is never silently deleted while the loader might still reference it.
            throw;
        }
    }

    private void TryLoadPlugin(string path, PluginManifest? manifest)
    {
        try
        {
            var context = CreateContext(path);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
            var types = DiscoverFactories(assembly);
            if (types.Count == 0) throw new InvalidDataException("DLL 未公开实现 IDriverFactory 的驱动工厂。");
            var loaded = types.Select(CreateFactory).ToArray();
            foreach (var factory in loaded) ValidateDescriptor(factory.Descriptor);
            lock (_sync)
            {
                var duplicate = loaded.FirstOrDefault(factory => _factories.ContainsKey(factory.Descriptor.Id));
                if (duplicate is not null) throw new InvalidOperationException($"驱动标识重复：{duplicate.Descriptor.Id}");
                foreach (var factory in loaded)
                {
                    _factories.Add(factory.Descriptor.Id, factory);
                    _log.Write(GatewayLogLevel.Information, $"加载扩展驱动：{factory.Descriptor.Name} ({factory.Descriptor.Id})");
                    if (manifest is not null)
                        _plugins[factory.Descriptor.Id] = new(factory.Descriptor.Id, factory.Descriptor.Name,
                            manifest.Version, Path.GetFileNameWithoutExtension(path), manifest.Sha256, "已加载");
                }
                _contexts.Add(context);
            }
        }
        catch (Exception ex)
        {
            _log.Write(GatewayLogLevel.Error, $"扩展驱动 {Path.GetFileName(path)} 加载失败：{SafeMessage(ex)}");
        }
    }

    private static List<Type> DiscoverFactories(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes().Where(type => type.IsPublic && !type.IsAbstract && !type.IsInterface
                && typeof(IDriverFactory).IsAssignableFrom(type)).ToList();
        }
        catch (ReflectionTypeLoadException ex)
        {
            throw new InvalidDataException("驱动依赖加载失败：" + string.Join("; ", ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.GetType().Name).Distinct()), ex);
        }
    }

    private static IDriverFactory CreateFactory(Type type) =>
        Activator.CreateInstance(type) as IDriverFactory
        ?? throw new InvalidDataException($"驱动工厂 {type.FullName} 必须具有公共无参构造函数。");

    private static void ValidateDescriptor(DriverDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id) || descriptor.Id.Length > 128
            || descriptor.Id.Any(char.IsControl) || string.IsNullOrWhiteSpace(descriptor.Name)
            || descriptor.Name.Length > 200)
            throw new InvalidDataException("驱动描述信息无效：Id 必须唯一，且名称不能为空。");
    }

    private static DriverLoadContext CreateContext(string path) => new(Path.GetFullPath(path));
    private static string SafeMessage(Exception exception) =>
        $"{exception.GetBaseException().GetType().Name}: {exception.GetBaseException().Message}";

    private sealed record PluginManifest(string AssemblyFile, string Sha256, string DriverId, string DriverName, string Version);

    private sealed class DriverLoadContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The host owns contract/model type identity. A plugin-local copy must never fork these types.
            if (assemblyName.Name == typeof(IDriverFactory).Assembly.GetName().Name) return typeof(IDriverFactory).Assembly;
            if (assemblyName.Name == typeof(DeviceConfiguration).Assembly.GetName().Name) return typeof(DeviceConfiguration).Assembly;
            var dependency = _resolver.ResolveAssemblyToPath(assemblyName);
            return dependency is null ? null : LoadFromAssemblyPath(dependency);
        }
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var dependency = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return dependency is null ? 0 : LoadUnmanagedDllFromPath(dependency);
        }
    }
}
