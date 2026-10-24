using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Application;

internal sealed class DriverCatalog : IDriverCatalog
{
    private readonly IDriverRegistry _registry;
    private readonly IReadOnlyDictionary<string, DriverDescriptor> _descriptors;

    public DriverCatalog(IDriverRegistry registry)
    {
        _registry = registry;
        // Registration finishes before composition. Freeze nested collections once so
        // timer-driven views can use metadata without cloning the complete catalog.
        Drivers = Array.AsReadOnly(registry.Drivers.Select(Freeze).ToArray());
        _descriptors = Drivers.ToDictionary(descriptor => descriptor.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DriverDescriptor> Drivers { get; }
    public DriverDescriptor GetDescriptor(string driverId) => _descriptors.TryGetValue(driverId, out var descriptor)
        ? descriptor : throw new InvalidOperationException($"未安装驱动：{driverId}");
    public IReadOnlyList<string> Validate(DeviceConfiguration configuration) =>
        _registry.GetFactory(configuration.DriverId).Validate(GatewayJson.Clone(configuration));
    public IReadOnlyList<string> ValidateConfiguration(GatewayConfiguration configuration) =>
        Service.ConfigurationValidator.Validate(GatewayJson.Clone(configuration), _registry);
    private static DriverDescriptor Freeze(DriverDescriptor descriptor) => descriptor with
    {
        Parameters = Array.AsReadOnly(descriptor.Parameters.Select(parameter => parameter with
        { Options = Array.AsReadOnly(parameter.Options.ToArray()) }).ToArray()),
        Methods = Array.AsReadOnly(descriptor.Methods.Select(method => method with
        { SupportedDataTypes = Array.AsReadOnly(method.SupportedDataTypes.ToArray()) }).ToArray()),
        SupportedDataTypes = Array.AsReadOnly(descriptor.SupportedDataTypes.ToArray())
    };
}
