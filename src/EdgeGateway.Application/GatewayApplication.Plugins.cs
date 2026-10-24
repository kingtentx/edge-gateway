using EdgeGateway.Abstractions;

namespace EdgeGateway.Application;

public sealed partial class GatewayApplication
{
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
