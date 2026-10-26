using EdgeGateway.Model;

namespace EdgeGateway.Abstractions;

public interface IConfigurationWorkbookFiles
{
    Task<DeviceConfigurationWorkbook> ReadDevicesAsync(string path, CancellationToken cancellationToken = default);
    Task WriteDevicesAsync(string path, DeviceConfigurationWorkbook workbook, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagConfiguration>> ReadTagsAsync(string path, CancellationToken cancellationToken = default);
    Task WriteTagsAsync(string path, IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken = default);
}
