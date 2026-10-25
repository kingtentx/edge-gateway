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
                _driverPluginManager?.IsBundledDriver(driver.Id) == true ? "bundled" : uploaded ? "uploaded" : saved?.Source ?? "registered", "已加载", true);
        }
        foreach (var plugin in plugins.Values.Where(plugin => !result.ContainsKey(plugin.Id)))
            result[plugin.Id] = new(plugin.Id, plugin.Name, plugin.Version, plugin.AssemblyName,
                plugin.Sha256, _driverPluginManager?.IsBundledDriver(plugin.Id) == true ? "bundled" : "uploaded",
                plugin.Status == "已加载" ? "未加载" : "未加载（" + plugin.Status + "）", false);
        return result.Values.OrderByDescending(driver => driver.IsAvailable)
            .ThenBy(driver => driver.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<DriverPluginInfo> GetInstalledDriverPlugins()
    {
        ThrowIfDisposed();
        return _driverPluginManager?.GetInstalledPlugins() ?? [];
    }

    public async Task<DriverDetails> GetDriverDetailsAsync(string driverId, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var list = await GetDriverListAsync(cancellationToken).ConfigureAwait(false);
            var driver = list.SingleOrDefault(item => string.Equals(item.Id, driverId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("驱动不存在，请刷新列表。");
            var relatedIds = _driverPluginManager?.GetRelatedDriverIds(driverId) ?? new[] { driverId };
            var affected = relatedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var references = _configuration.Devices.Where(device => affected.Contains(device.DriverId))
                .Select(device => new DriverDeviceReference(device.Id, device.Name, _runtime.IsDeviceRunning(device.Id))).ToArray();
            return new(driver, driver.IsAvailable ? Drivers.GetDescriptor(driverId) : null, references, relatedIds);
        }
        finally { _commands.Release(); }
    }

    public Task<string> InstallDriverPluginAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return ChangeDriverPluginAsync(null, sourcePath, cancellationToken);
    }

    public Task<string> UpdateDriverPluginAsync(string driverId, string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return ChangeDriverPluginAsync(driverId, sourcePath, cancellationToken);
    }

    public Task<string> UninstallDriverPluginAsync(string driverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        return ChangeDriverPluginAsync(driverId, null, cancellationToken);
    }

    private async Task<string> ChangeDriverPluginAsync(string? driverId, string? sourcePath, CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await _runtime.ChangeDriverPluginAsync(driverId, sourcePath, cancellationToken).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }
}
