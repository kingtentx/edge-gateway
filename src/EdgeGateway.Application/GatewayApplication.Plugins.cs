using EdgeGateway.Abstractions;

namespace EdgeGateway.Application;

public sealed partial class GatewayApplication
{
    public async Task<IReadOnlyList<DriverListItem>> GetDriverListAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var inventory = _configurationStore is IDriverInventoryStore store
            ? await store.GetDriverInventoryAsync(cancellationToken).ConfigureAwait(false)
            : Array.Empty<DriverInventoryRecord>();
        var metadata = inventory.ToDictionary(driver => driver.Id, StringComparer.OrdinalIgnoreCase);
        var plugins = GetInstalledDriverPlugins().ToDictionary(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, DriverListItem>(StringComparer.OrdinalIgnoreCase);
        // The live catalog decides availability. Startup metadata supplies bundled DLL details;
        // newly installed plugins are merged immediately without requiring a host restart.
        foreach (var driver in Drivers.Drivers)
        {
            metadata.TryGetValue(driver.Id, out var saved);
            plugins.TryGetValue(driver.Id, out var plugin);
            var uploaded = plugin is { Status: "已加载" };
            result[driver.Id] = new(driver.Id, driver.Name,
                uploaded ? plugin!.Version : saved?.Version ?? "unknown",
                uploaded ? plugin!.AssemblyName : saved?.AssemblyName ?? "",
                uploaded ? plugin!.Sha256 : saved?.Sha256 ?? "",
                uploaded ? "uploaded" : saved?.Source ?? "registered", "已加载", true);
        }
        foreach (var plugin in plugins.Values.Where(plugin => !result.ContainsKey(plugin.Id)))
            result[plugin.Id] = new(plugin.Id, plugin.Name, plugin.Version, plugin.AssemblyName,
                plugin.Sha256, "uploaded", plugin.Status == "已加载" ? "未加载" : "未加载（" + plugin.Status + "）", false);
        return result.Values.OrderByDescending(driver => driver.IsAvailable)
            .ThenBy(driver => driver.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<DriverPluginInfo> GetInstalledDriverPlugins()
    {
        ThrowIfDisposed();
        return _driverPluginManager?.GetInstalledPlugins() ?? [];
    }

    public string InstallDriverPlugin(string sourcePath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (_driverPluginManager is null)
            throw new NotSupportedException("当前宿主未启用驱动插件安装功能。");
        var result = _driverPluginManager.InstallPlugin(sourcePath);
        // DriverCatalog reads the registry live; a subsequent editor open sees the new descriptor.
        return result;
    }
}
