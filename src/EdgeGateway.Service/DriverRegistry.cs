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
    private readonly Dictionary<string, string> _manifestPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssemblyLoadContext> _pluginContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bundledAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<Assembly, IReadOnlyList<IDriverFactory>>? _adaptAssembly;
    private readonly IGatewayLog _log;
    private readonly IDriverPluginStore? _pluginStore;
    private string? _pluginDirectory;

    public DriverRegistry(IEnumerable<IDriverFactory> factories, IGatewayLog log, IDriverPluginStore? pluginStore = null,
        IReadOnlyDictionary<string, string>? bundledAssemblies = null,
        Func<Assembly, IReadOnlyList<IDriverFactory>>? adaptAssembly = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _pluginStore = pluginStore;
        _adaptAssembly = adaptAssembly;
        if (bundledAssemblies is not null)
            foreach (var pair in bundledAssemblies) _bundledAssemblies[pair.Key] = pair.Value;
        if (_pluginStore is not null)
        {
            try
            {
                foreach (var plugin in _pluginStore.GetDriverPluginsAsync().GetAwaiter().GetResult())
                    _plugins[plugin.Id] = plugin with { Status = "等待文件校验" };
                _managedIds.UnionWith(_plugins.Keys);
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

    public void Register(IDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ValidateDescriptor(factory.Descriptor);
        lock (_sync)
        {
            _bundledAssemblies.TryAdd(factory.Descriptor.Id, factory.GetType().Assembly.GetName().Name!);
            if (_managedIds.Contains(factory.Descriptor.Id)) return;
            if (!_factories.TryAdd(factory.Descriptor.Id, factory))
                throw new InvalidOperationException($"驱动标识重复：{factory.Descriptor.Id}");
        }
    }

    public IReadOnlyList<DriverPluginInfo> GetInstalledPlugins()
    {
        lock (_sync) return _plugins.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlySet<string> ManagedDriverIds { get { lock (_sync) return new HashSet<string>(_managedIds, StringComparer.OrdinalIgnoreCase); } }

    public bool IsBundledDriver(string driverId) { lock (_sync) return _bundledAssemblies.ContainsKey(driverId); }

    public IReadOnlyList<string> GetRelatedDriverIds(string driverId)
    {
        lock (_sync)
        {
            if (_groups.TryGetValue(driverId, out var group)) return group.ToArray();
            if (_bundledAssemblies.TryGetValue(driverId, out var assembly))
                return _bundledAssemblies.Where(pair => string.Equals(pair.Value, assembly, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            return [driverId];
        }
    }

    // Called for all deployment roots before loading any baseline assemblies. Managed updates and
    // deletions suppress the baseline, including invalid updates: never silently use an old version.
    public void PrepareManagedPlugins(params string[] directories)
    {
        foreach (var directory in directories)
        {
            _directories.Add(Path.GetFullPath(directory));
            var installed = Path.Combine(directory, "installed");
            if (!Directory.Exists(installed)) continue;
            foreach (var path in Directory.EnumerateFiles(installed, "driver-plugin.json", SearchOption.AllDirectories))
            {
                // A corrupted deletion/update manifest must not reactivate a shipped baseline.
                foreach (var group in _bundledAssemblies.GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    var ids = group.Select(pair => pair.Key).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
                    var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ids[0].ToUpperInvariant())));
                    if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), key, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var id in ids) { _managedIds.Add(id); _manifestPaths[id] = path; _groups[id] = ids; }
                }
                try
                {
                    var manifest = ReadManifest(path);
                    var ids = ManifestIds(manifest);
                    foreach (var id in ids)
                    {
                        _manifestPaths[id] = path;
                        _groups[id] = ids;
                        _managedIds.Add(id);
                        if (manifest.Bundled) _bundledAssemblies[id] = ManifestAssembly(manifest);
                    }
                }
                catch (Exception ex) { _log.Write(GatewayLogLevel.Error, "读取驱动激活清单失败：" + SafeMessage(ex)); }
            }
        }
    }

    public void LoadPlugins(string directory)
    {
        _pluginDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_pluginDirectory);
        PrepareManagedPlugins(directory);
        foreach (var id in _managedIds.Where(id => !_pluginContexts.ContainsKey(id))) _factories.Remove(id);
        var installed = Path.Combine(_pluginDirectory, "installed");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var file in Directory.EnumerateFiles(_pluginDirectory, "EdgeGateway.Driver.*.dll", SearchOption.AllDirectories)
            .Where(file => !Path.GetFullPath(file).StartsWith(Path.GetFullPath(installed) + Path.DirectorySeparatorChar, comparison)))
        {
            var assemblyName = Path.GetFileNameWithoutExtension(file);
            if (_bundledAssemblies.Any(pair => _managedIds.Contains(pair.Key)
                && string.Equals(pair.Value, assemblyName, StringComparison.OrdinalIgnoreCase))) continue;
            TryLoadPlugin(file, null);
        }
        if (!Directory.Exists(installed)) return;
        foreach (var path in Directory.EnumerateFiles(installed, "driver-plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = ReadManifest(path);
                var ids = ManifestIds(manifest);
                if (!manifest.Active)
                {
                    _pluginStore?.ReplaceDriverPluginsAsync(ids, []).GetAwaiter().GetResult();
                    foreach (var id in ids) _plugins.Remove(id);
                    if (manifest.Bundled) TryDeleteBundledBaseline(ManifestAssembly(manifest));
                    continue;
                }
                foreach (var id in ids)
                    _plugins[id] = new(id, _plugins.GetValueOrDefault(id)?.Name ?? manifest.DriverName, manifest.Version,
                        ManifestAssembly(manifest), manifest.Sha256, "等待文件校验");
                var root = Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar;
                var dll = Path.GetFullPath(Path.Combine(root, manifest.AssemblyFile));
                if (!dll.StartsWith(root, comparison) || !File.Exists(dll)) throw new InvalidDataException("驱动清单引用的 DLL 路径无效。");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
                if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("驱动文件 SHA-256 与安装清单不一致。");
                TryLoadPlugin(dll, manifest);
            }
            catch (Exception ex) { _log.Write(GatewayLogLevel.Error, "驱动插件清单加载失败：" + SafeMessage(ex)); }
        }
    }

    private static PluginManifest ReadManifest(string path) => JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path))
        ?? throw new InvalidDataException("驱动清单为空。");
    private static string[] ManifestIds(PluginManifest manifest) => manifest.DriverIds is { Length: > 0 } ? manifest.DriverIds : [manifest.DriverId];
    private static string ManifestAssembly(PluginManifest manifest) => manifest.AssemblyName ?? Path.GetFileNameWithoutExtension(manifest.AssemblyFile);

    public string InstallPlugin(string sourcePath) => InstallCore(null, sourcePath, null);

    public string UpdatePlugin(string driverId, string sourcePath, GatewayConfiguration configuration) =>
        InstallCore(driverId, sourcePath, configuration);

    private string InstallCore(string? driverId, string sourcePath, GatewayConfiguration? configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        lock (_sync)
        {
            if (_pluginDirectory is null) throw new InvalidOperationException("驱动插件目录尚未初始化。");
            DriverPluginInfo? previous = null;
            if (driverId is not null) previous = GetManagedInfo(driverId);
            var source = new FileInfo(Path.GetFullPath(sourcePath));
            if (!source.Exists || !string.Equals(source.Extension, ".dll", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择有效的 .dll 驱动文件。", nameof(sourcePath));
            if (source.Length is < 1 or > 100L * 1024 * 1024)
                throw new ArgumentException("驱动 DLL 必须大于 0 字节且不超过 100 M。", nameof(sourcePath));

            // Inspect exactly the staged bytes; the source may change after the file picker returns.
            var stagingDirectory = Path.Combine(_pluginDirectory, ".staging");
            Directory.CreateDirectory(stagingDirectory);
            var staged = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".dll");
            AssemblyLoadContext? context = null;
            string? destination = null;
            var committed = false;
            try
            {
                File.Copy(source.FullName, staged);
                if (new FileInfo(staged).Length is < 1 or > 100L * 1024 * 1024)
                    throw new InvalidDataException("驱动 DLL 大小不合法。");
                var name = AssemblyName.GetAssemblyName(staged);
                var simpleName = name.Name ?? throw new BadImageFormatException("无法读取 DLL 程序集名称。");
                if (simpleName.Length > 100 || simpleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new BadImageFormatException("DLL 程序集名称不合法。");
                var version = name.Version ?? new Version(0, 0, 0, 0);
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staged)));
                context = CreateContext(staged);
                Assembly assembly;
                using (var stream = File.OpenRead(staged)) assembly = context.LoadFromStream(stream);
                var factories = ResolveFactories(assembly);
                var ids = factories.Select(factory => factory.Descriptor.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
                if (ids.Length == 0 || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
                    throw new InvalidDataException("DLL 必须公开有效且标识不重复的驱动工厂。");
                var id = ids[0];
                var factory = factories.Single(item => string.Equals(item.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));
                var bundled = ids.Any(IsBundledDriver);
                var expected = bundled ? GetRelatedDriverIds(ids.First(IsBundledDriver)) : new[] { id };
                if (!ids.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected))
                    throw new InvalidDataException("驱动 DLL 必须保留该程序集的全部驱动标识；新上传插件只允许一个驱动工厂。");
                if (bundled && ids.Any(member => !string.Equals(_bundledAssemblies.GetValueOrDefault(member), simpleName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("内置驱动 DLL 的程序集名称必须保持不变。");
                if (driverId is null)
                {
                    if (ids.Any(member => _factories.ContainsKey(member) || _plugins.ContainsKey(member)))
                        throw new InvalidOperationException($"驱动标识“{id}”已存在，请选择该驱动后使用“更新 DLL”。");
                }
                else
                {
                    if (!ids.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(GetRelatedDriverIds(driverId)))
                        throw new InvalidDataException("更新 DLL 的驱动标识必须与所选驱动一致。");
                    if (!string.Equals(simpleName, previous!.AssemblyName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新 DLL 的程序集名称必须与原插件一致。");
                    if (!Version.TryParse(previous.Version, out var oldVersion) || version <= oldVersion)
                        throw new InvalidDataException("更新 DLL 的程序集版本必须高于当前版本。");
                    var errors = ConfigurationValidator.Validate(configuration ?? throw new ArgumentNullException(nameof(configuration)),
                        new ReplacementRegistry(this, factories));
                    if (errors.Count > 0) throw new InvalidDataException("新驱动不兼容现有设备配置：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
                }

                // One stable activation manifest per driver. Old revision DLLs have no manifests,
                // so neither directory order nor a process crash can reactivate an older revision.
                if (!_manifestPaths.TryGetValue(id, out var manifestPath))
                {
                    var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToUpperInvariant())));
                    manifestPath = Path.Combine(_pluginDirectory, "installed", key, "driver-plugin.json");
                }
                var root = Path.GetDirectoryName(manifestPath)!;
                var revision = Path.Combine(root, "revisions", version + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(revision);
                destination = Path.Combine(revision, simpleName + ".dll");
                File.Move(staged, destination);
                var manifest = new PluginManifest(Path.GetRelativePath(root, destination), hash, id, factory.Descriptor.Name, version.ToString(), true, ids, simpleName, bundled);
                var plugins = factories.Select(item => new DriverPluginInfo(item.Descriptor.Id, item.Descriptor.Name, version.ToString(), simpleName, hash, "已加载")).ToArray();
                var oldManifest = ReadManifestForCleanup(manifestPath);
                CommitManifest(manifestPath, manifest, () => _pluginStore?.ReplaceDriverPluginsAsync(ids, plugins).GetAwaiter().GetResult());
                foreach (var member in ids) ReleasePluginContext(member);
                foreach (var item in factories) _factories[item.Descriptor.Id] = item;
                foreach (var plugin in plugins)
                {
                    _plugins[plugin.Id] = plugin;
                    _manifestPaths[plugin.Id] = manifestPath;
                    _pluginContexts[plugin.Id] = context;
                    _groups[plugin.Id] = ids;
                    _managedIds.Add(plugin.Id);
                }
                _contexts.Add(context);
                committed = true;
                TryDeleteAssembly(manifestPath, oldManifest, destination);
                if (bundled) TryDeleteBundledBaseline(simpleName);
                _log.Write(GatewayLogLevel.Information, $"驱动插件{(driverId is null ? "安装" : "更新")}：{id}，版本 {version}，SHA-256 {hash}");
                return $"驱动 {factory.Descriptor.Name} ({id}) 版本 {version} 已{(driverId is null ? "安装并立即注册" : "更新并生效")}。";
            }
            finally
            {
                if (!committed) context?.Unload();
                TryDeleteFile(staged);
                // Unreferenced revisions are never discovered. Preserve the candidate if a disk
                // failure also prevented manifest rollback; deleting it could break crash recovery.
            }
        }
    }

    public string UninstallPlugin(string driverId)
    {
        lock (_sync)
        {
            var plugin = GetManagedInfo(driverId);
            var ids = GetRelatedDriverIds(driverId).ToArray();
            if (_pluginDirectory is null) throw new InvalidOperationException("驱动目录尚未初始化。");
            if (!_manifestPaths.TryGetValue(driverId, out var path))
            {
                var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ids[0].ToUpperInvariant())));
                path = Path.Combine(_pluginDirectory, "installed", key, "driver-plugin.json");
            }
            var old = ReadManifestForCleanup(path);
            var inactive = new PluginManifest("", plugin.Sha256, ids[0], plugin.Name, plugin.Version, false, ids, plugin.AssemblyName, IsBundledDriver(driverId));
            CommitManifest(path, inactive, () => _pluginStore?.ReplaceDriverPluginsAsync(ids, []).GetAwaiter().GetResult());
            foreach (var id in ids)
            {
                _factories.Remove(id);
                _plugins.Remove(id);
                _manifestPaths[id] = path;
                _groups[id] = ids;
                _managedIds.Add(id);
                ReleasePluginContext(id);
            }
            TryDeleteAssembly(path, old);
            if (IsBundledDriver(driverId)) TryDeleteBundledBaseline(plugin.AssemblyName);
            _log.Write(GatewayLogLevel.Information, "驱动已删除：" + string.Join("、", ids));
            return "驱动已删除，重启后不会重新加载：" + string.Join("、", ids);
        }
    }

    private DriverPluginInfo GetManagedInfo(string id)
    {
        if (_plugins.TryGetValue(id, out var plugin)) return plugin;
        if (!_factories.TryGetValue(id, out var factory)) throw new InvalidOperationException("驱动不存在，请刷新列表。");
        var assemblyName = _bundledAssemblies.GetValueOrDefault(id) ?? factory.GetType().Assembly.GetName().Name!;
        var path = _directories.Select(directory => Path.Combine(directory, assemblyName + ".dll")).FirstOrDefault(File.Exists);
        if (path is null)
            path = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))?.Location;
        var version = !string.IsNullOrEmpty(path) && File.Exists(path) ? AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "0.0.0.0" : "0.0.0.0";
        var hash = !string.IsNullOrEmpty(path) && File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "";
        return new(id, factory.Descriptor.Name, version, assemblyName, hash, "已加载");
    }

    private void ReleasePluginContext(string id)
    {
        if (!_pluginContexts.Remove(id, out var context)) return;
        if (_pluginContexts.Values.Contains(context)) return;
        _contexts.Remove(context);
        context.Unload(); // Existing references control actual reclamation; never force GC on the UI path.
    }

    private void TryDeleteBundledBaseline(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName) || assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
        foreach (var directory in _directories) TryDeleteFile(Path.Combine(directory, assemblyName + ".dll"));
    }

    private static void CommitManifest(string path, PluginManifest manifest, Action persist)
    {
        var original = File.Exists(path) ? File.ReadAllText(path) : null;
        WriteManifest(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        try { persist(); }
        catch
        {
            if (original is null) File.Delete(path);
            else WriteManifest(path, original);
            throw;
        }
    }

    private static void WriteManifest(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDeleteFile(temporary); }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static PluginManifest? ReadManifestForCleanup(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path)) : null; }
        catch (JsonException) { return null; }
    }

    private static void TryDeleteAssembly(string manifestPath, PluginManifest? manifest, string? keep = null)
    {
        if (manifest is not { Active: true } || string.IsNullOrWhiteSpace(manifest.AssemblyFile)) return;
        try
        {
            var root = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!) + Path.DirectorySeparatorChar;
            var dll = Path.GetFullPath(Path.Combine(root, manifest.AssemblyFile));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (dll.StartsWith(root, comparison) && string.Equals(Path.GetExtension(dll), ".dll", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(dll, keep, comparison)) TryDeleteFile(dll);
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }

    private sealed class ReplacementRegistry(DriverRegistry registry, IReadOnlyList<IDriverFactory> replacements) : IDriverRegistry
    {
        private readonly Dictionary<string, IDriverFactory> _replacements = replacements.ToDictionary(factory => factory.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<DriverDescriptor> Drivers => registry.Drivers.Where(driver => !_replacements.ContainsKey(driver.Id))
            .Concat(replacements.Select(factory => factory.Descriptor)).ToArray();
        public IDriverFactory GetFactory(string id) => _replacements.GetValueOrDefault(id) ?? registry.GetFactory(id);
    }

    private IReadOnlyList<IDriverFactory> ResolveFactories(Assembly assembly)
    {
        var types = DiscoverFactories(assembly);
        var factories = types.Count > 0 ? types.Select(CreateFactory).ToArray() : _adaptAssembly?.Invoke(assembly) ?? [];
        foreach (var factory in factories) ValidateDescriptor(factory.Descriptor);
        return factories;
    }

    private void TryLoadPlugin(string path, PluginManifest? manifest)
    {
        AssemblyLoadContext? context = null;
        var registered = false;
        try
        {
            context = CreateContext(path);
            Assembly assembly;
            if (manifest is null) assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
            else { using var stream = File.OpenRead(path); assembly = context.LoadFromStream(stream); }
            var loaded = ResolveFactories(assembly);
            if (loaded.Count == 0) throw new InvalidDataException("DLL 未公开可识别的驱动工厂。");
            if (manifest is not null && (!loaded.Select(factory => factory.Descriptor.Id).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(ManifestIds(manifest))
                || !loaded.Any(factory => factory.Descriptor.Id.Equals(manifest.DriverId, StringComparison.OrdinalIgnoreCase) && factory.Descriptor.Name == manifest.DriverName)
                || assembly.GetName().Version?.ToString() != manifest.Version || assembly.GetName().Name != ManifestAssembly(manifest)))
                throw new InvalidDataException("驱动清单与程序集内的身份、名称或版本不一致。");
            lock (_sync)
            {
                var duplicate = loaded.FirstOrDefault(factory => _factories.ContainsKey(factory.Descriptor.Id));
                if (duplicate is not null) throw new InvalidOperationException($"驱动标识重复：{duplicate.Descriptor.Id}");
                if (manifest is not null)
                {
                    var plugins = loaded.Select(factory => new DriverPluginInfo(factory.Descriptor.Id, factory.Descriptor.Name,
                        manifest.Version, ManifestAssembly(manifest), manifest.Sha256, "已加载")).ToArray();
                    _pluginStore?.ReplaceDriverPluginsAsync(ManifestIds(manifest), plugins).GetAwaiter().GetResult();
                    foreach (var plugin in plugins) { _plugins[plugin.Id] = plugin; _pluginContexts[plugin.Id] = context; }
                }
                foreach (var factory in loaded)
                {
                    _factories.Add(factory.Descriptor.Id, factory);
                    if (manifest is null)
                    {
                        _bundledAssemblies[factory.Descriptor.Id] = assembly.GetName().Name!;
                        _pluginContexts[factory.Descriptor.Id] = context;
                    }
                    _log.Write(GatewayLogLevel.Information, $"加载驱动：{factory.Descriptor.Name} ({factory.Descriptor.Id})");
                }
                _contexts.Add(context);
                registered = true;
                if (manifest?.Bundled == true) TryDeleteBundledBaseline(ManifestAssembly(manifest));
            }
        }
        catch (Exception ex) { _log.Write(GatewayLogLevel.Error, $"驱动 {Path.GetFileName(path)} 加载失败：{SafeMessage(ex)}"); }
        finally { if (!registered) context?.Unload(); }
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

    private sealed record PluginManifest(string AssemblyFile, string Sha256, string DriverId, string DriverName, string Version, bool Active = true, string[]? DriverIds = null, string? AssemblyName = null, bool Bundled = false);

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
