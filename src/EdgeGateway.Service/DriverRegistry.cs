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
    private readonly IDriverPluginStore? _pluginStore;
    private string? _pluginDirectory;

    public DriverRegistry(IEnumerable<IDriverFactory> factories, IGatewayLog log, IDriverPluginStore? pluginStore = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _pluginStore = pluginStore;
        if (_pluginStore is not null)
        {
            try
            {
                foreach (var plugin in _pluginStore.GetDriverPluginsAsync().GetAwaiter().GetResult())
                    _plugins[plugin.Id] = plugin with { Status = "等待文件校验" };
            }
            catch (Exception exception)
            {
                _log.Write(GatewayLogLevel.Error, $"读取 SQLite 驱动清单失败：{SafeMessage(exception)}");
                throw new InvalidOperationException("无法读取 SQLite 中的驱动注册信息，已停止初始化以避免配置与驱动状态不一致。", exception);
            }
        }
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

    private void Register(IDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ValidateDescriptor(factory.Descriptor);
        lock (_sync)
            if (!_factories.TryAdd(factory.Descriptor.Id, factory))
                throw new InvalidOperationException($"驱动标识重复：{factory.Descriptor.Id}");
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
        var installed = Path.Combine(_pluginDirectory, "installed");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var file in Directory.EnumerateFiles(_pluginDirectory, "EdgeGateway.Driver.*.dll", SearchOption.AllDirectories)
            .Where(file => !Path.GetFullPath(file).StartsWith(Path.GetFullPath(installed) + Path.DirectorySeparatorChar, comparison)))
            TryLoadPlugin(file, null);


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
        else
        {
            // Stage beside the final DLL and atomically rename only after the copied bytes match.
            var temporaryAssembly = destination + "." + Guid.NewGuid().ToString("N") + ".upload";
            try
            {
                File.Copy(source, temporaryAssembly, overwrite: false);
                var copiedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(temporaryAssembly)));
                if (!string.Equals(copiedHash, sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("上传期间 DLL 内容发生变化，安装已取消。");
                File.Move(temporaryAssembly, destination, overwrite: false);
            }
            finally { try { if (File.Exists(temporaryAssembly)) File.Delete(temporaryAssembly); } catch { } }
        }

        PluginManifest? manifest = null;
        var registered = false;
        try
        {
            var context = CreateContext(destination);
            var assembly = context.LoadFromAssemblyPath(destination);
            var factories = DiscoverFactories(assembly);
            if (factories.Count != 1)
                throw new InvalidDataException($"一个驱动 DLL 必须且只能公开一个 IDriverFactory；当前找到 {factories.Count} 个。");
            var factory = CreateFactory(factories[0]);
            ValidateDescriptor(factory.Descriptor);
            manifest = new(simpleName + ".dll", sha256, factory.Descriptor.Id, factory.Descriptor.Name, version);
            var manifestPath = Path.Combine(installedRoot, "driver-plugin.json");
            lock (_sync)
            {
                if (_factories.ContainsKey(factory.Descriptor.Id))
                    throw new InvalidOperationException($"驱动标识“{factory.Descriptor.Id}”已注册。为避免影响正在运行的设备，本版本不允许覆盖同 ID 驱动；请使用唯一驱动 ID。");
                var temporaryManifest = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporaryManifest, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temporaryManifest, manifestPath, overwrite: true);
                    _factories.Add(factory.Descriptor.Id, factory);
                    _contexts.Add(context);
                    _plugins[factory.Descriptor.Id] = new(factory.Descriptor.Id, factory.Descriptor.Name, version, simpleName, sha256, "已加载");
                    registered = true;
                }
                catch
                {
                    try { if (File.Exists(temporaryManifest)) File.Delete(temporaryManifest); } catch { }
                    throw;
                }
            }
            _log.Write(GatewayLogLevel.Information, $"驱动插件已安装并热加载：{factory.Descriptor.Name} ({factory.Descriptor.Id})，版本 {version}，SHA-256 {sha256}");
            return $"驱动 {factory.Descriptor.Name} ({factory.Descriptor.Id}) 版本 {version} 已安装并立即注册。无需重启网关；新建该类型设备前请先完成驱动参数与通信验证。";
        }
        catch
        {
            // A failed validation must not permanently poison this assembly-version slot.
            // If the runtime has already registered it, keep its on-disk files intact.
            if (!registered && !File.Exists(Path.Combine(installedRoot, "driver-plugin.json")))
                try { File.Delete(destination); } catch { /* A loaded assembly may still hold the file on Windows. */ }
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
            if (manifest is not null && (loaded.Length != 1
                || !string.Equals(loaded[0].Descriptor.Id, manifest.DriverId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(loaded[0].Descriptor.Name, manifest.DriverName, StringComparison.Ordinal)))
                throw new InvalidDataException("驱动清单与程序集内的驱动标识/名称不一致。");
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

    private sealed class DriverLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver? _resolver;
        public DriverLoadContext(string path) : base(isCollectible: true)
        {
            // A genuinely single-file plugin has no .deps.json. In that case framework and
            // host contract references resolve from the default context; private dependencies
            // must be deployed with a deps manifest and are not silently bundled by upload.
            if (File.Exists(Path.ChangeExtension(path, ".deps.json"))) _resolver = new AssemblyDependencyResolver(path);
        }
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The host owns contract/model type identity. A plugin-local copy must never fork these types.
            if (assemblyName.Name == typeof(IDriverFactory).Assembly.GetName().Name) return typeof(IDriverFactory).Assembly;
            if (assemblyName.Name == typeof(DeviceConfiguration).Assembly.GetName().Name) return typeof(DeviceConfiguration).Assembly;
            var dependency = _resolver?.ResolveAssemblyToPath(assemblyName);
            return dependency is null ? null : LoadFromAssemblyPath(dependency);
        }
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var dependency = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            return dependency is null ? 0 : LoadUnmanagedDllFromPath(dependency);
        }
    }
}
