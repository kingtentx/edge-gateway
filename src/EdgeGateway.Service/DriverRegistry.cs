using System.Reflection;
using System.Runtime.Loader;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed class DriverRegistry : IDriverRegistry
{
    private readonly Dictionary<string, IDriverFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AssemblyLoadContext> _contexts = [];
    private readonly IGatewayLog _log;

    public DriverRegistry(IEnumerable<IDriverFactory> factories, IGatewayLog log)
    {
        _log = log;
        foreach (var factory in factories) Register(factory);
    }

    public IReadOnlyList<DriverDescriptor> Drivers => _factories.Values.Select(f => f.Descriptor).OrderBy(d => d.Id).ToArray();
    public IDriverFactory GetFactory(string driverId) => _factories.TryGetValue(driverId, out var factory) ? factory :
        throw new InvalidOperationException($"未安装驱动：{driverId}");

    /// <summary>Load trusted local plugins only, before constructing/starting a runtime. Restart is required after replacement.</summary>
    public void LoadPlugins(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "EdgeGateway.Driver.*.dll", SearchOption.AllDirectories))
        {
            try
            {
                var context = new DriverLoadContext(Path.GetFullPath(file));
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(file));
                var factories = assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface &&
                    typeof(IDriverFactory).IsAssignableFrom(t));
                foreach (var type in factories)
                {
                    if (Activator.CreateInstance(type) is not IDriverFactory factory)
                        throw new InvalidOperationException($"驱动工厂 {type.Name} 需要公共无参构造函数。");
                    Register(factory);
                    _log.Write(GatewayLogLevel.Information, $"加载扩展驱动：{factory.Descriptor.Name} ({factory.Descriptor.Id})");
                }
                _contexts.Add(context);
            }
            catch (Exception ex)
            {
                _log.Write(GatewayLogLevel.Error, $"扩展驱动 {Path.GetFileName(file)} 加载失败：{ex.GetBaseException().Message}");
            }
        }
    }

    private void Register(IDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(factory.Descriptor.Id)) throw new ArgumentException("驱动标识不能为空。");
        if (!_factories.TryAdd(factory.Descriptor.Id, factory)) throw new InvalidOperationException($"驱动标识重复：{factory.Descriptor.Id}");
    }

    private sealed class DriverLoadContext(string path) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // All plugins use the host's contract/model assemblies, never private copies with different type identity.
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
