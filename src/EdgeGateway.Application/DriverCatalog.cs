using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Application;

internal sealed class DriverCatalog : IDriverCatalog
{
    private readonly IDriverRegistry _registry;
    public DriverCatalog(IDriverRegistry registry) => _registry = registry;

    // The catalog is intentionally live: a newly installed DLL becomes available to editors
    // immediately, while each returned snapshot remains immutable to its caller.
    public IReadOnlyList<DriverDescriptor> Drivers => Array.AsReadOnly(_registry.Drivers.Select(Freeze).ToArray());
    public DriverDescriptor GetDescriptor(string driverId) => Freeze(_registry.GetFactory(driverId).Descriptor);
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
