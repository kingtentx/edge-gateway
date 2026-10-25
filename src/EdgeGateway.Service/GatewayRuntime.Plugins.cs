using EdgeGateway.Abstractions;

namespace EdgeGateway.Service;

public sealed partial class GatewayRuntime
{
    /// <summary>Shares admission with configuration changes, starts, stops and stopped-device probes.</summary>
    public async Task<string> ChangeDriverPluginAsync(string? driverId, string? sourcePath,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registry is not IDriverPluginManager manager)
                throw new NotSupportedException("当前宿主未启用驱动插件管理功能。");
            if (driverId is not null)
            {
                var affected = manager.GetRelatedDriverIds(driverId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var references = _configuration.Devices.Where(device =>
                    affected.Contains(device.DriverId)).ToArray();
                if (sourcePath is null && references.Length > 0)
                    throw new InvalidOperationException("驱动仍被设备引用，请先删除设备或更换驱动：" + string.Join("、", references.Select(d => d.Name)));
                var active = references.Where(device => IsDeviceRunning(device.Id)).ToArray();
                if (active.Length > 0)
                    throw new InvalidOperationException("请先停止使用该驱动的设备：" + string.Join("、", active.Select(d => d.Name)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Once file/metadata commit starts, finish it without cancellation between durable and live state.
            return await Task.Run(() => driverId is null
                ? manager.InstallPlugin(sourcePath ?? throw new ArgumentNullException(nameof(sourcePath)))
                : sourcePath is null ? manager.UninstallPlugin(driverId)
                : manager.UpdatePlugin(driverId, sourcePath, Clone(_configuration)), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
}
