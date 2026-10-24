using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Driver.TestPlugin;

public sealed class TestPluginFactory : IDriverFactory
{
    public DriverDescriptor Descriptor { get; } = new("test-dynamic-plugin", "Dynamic plugin fixture", "Used only to verify upload and restart registration.", false);
    public IDeviceDriver Create(DeviceConfiguration configuration) => new TestPluginDriver();
    public IReadOnlyList<string> Validate(DeviceConfiguration configuration) => [];
}

internal sealed class TestPluginDriver : IDeviceDriver
{
    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TagReadResult>>(tags.Select(tag => new TagReadResult(tag.Id, null, SampleQuality.Bad, "Test fixture does not connect to hardware.")).ToArray());
    public Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Test fixture is read-only.");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
