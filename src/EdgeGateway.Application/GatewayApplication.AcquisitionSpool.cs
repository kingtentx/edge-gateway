using EdgeGateway.Abstractions;

namespace EdgeGateway.Application;

public sealed partial class GatewayApplication
{
    public Task<AcquisitionSpoolStatistics> GetAcquisitionSpoolStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return _acquisitionSpool?.GetStatisticsAsync(cancellationToken)
            ?? Task.FromResult(new AcquisitionSpoolStatistics(0, 0, 0, 0, 0, "宿主未配置采集持久缓冲。"));
    }
}
