using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Driver.Modbus;
using EdgeGateway.IotGateway;
using EdgeGateway.Model;
using EdgeGateway.Service;
using PluginInterface;

namespace EdgeGateway.Tests.IotGateway;

public sealed class CatalogFixture
{
    public IReadOnlyList<IDriverFactory> Factories { get; } = IotGatewayDriverCatalog.CreateFactories(new GatewayLog());
    public IDriverFactory Get(string id) => Factories.Single(f => f.Descriptor.Id == id);
}

public sealed class CatalogTests(CatalogFixture catalog) : IClassFixture<CatalogFixture>
{
    [Fact]
    public void AllTwelveOriginalDriversPreserveEveryManifestMethodAndParameter()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "driver-migration-manifest.json")));
        var originals = manifest.RootElement.GetProperty("drivers").EnumerateArray().ToArray();
        Assert.Equal(12, originals.Length);
        Assert.Equal(12, catalog.Factories.Count);
        Assert.Equal(12, catalog.Factories.Select(f => f.Descriptor.Id).Distinct().Count());
        var methodCount = 0;
        var parameterCount = 0;
        foreach (var original in originals)
        {
            var factory = catalog.Get(original.GetProperty("directory").GetString()!);
            var descriptor = factory.Descriptor;
            Assert.True(descriptor.IsLegacy);
            foreach (var method in original.GetProperty("readMethods").EnumerateArray())
            {
                Assert.Contains(descriptor.Methods, m => m.Name == method.GetString());
                methodCount++;
            }
            foreach (var parameter in original.GetProperty("configurationParameters").EnumerateArray())
            {
                Assert.Contains(descriptor.Parameters, p => p.Key == parameter.GetString());
                parameterCount++;
            }
            var defaultMethod = Assert.Single(descriptor.Methods, m => m.Name == descriptor.DefaultReadMethod);
            Assert.False(defaultMethod.IsDiagnostic);
            Assert.Contains(defaultMethod.DefaultDataType, defaultMethod.SupportedDataTypes);
            Assert.All(descriptor.Parameters, p => Assert.False(string.IsNullOrWhiteSpace(p.TypeName)));
        }
        Assert.Equal(65, methodCount);
        Assert.Equal(74, parameterCount);
    }

    [Fact]
    public void MethodCapabilitiesPreserveBatchWireTypesAndDoNotInventWrites()
    {
        var mc = catalog.Get("PLC.MelsecMc").Descriptor;
        Assert.Contains(TagDataType.Float32, mc.Methods.Single(m => m.Name == "ReadMultiple").SupportedDataTypes);
        Assert.Contains(TagDataType.Boolean, mc.Methods.Single(m => m.Name == "ReadMultiple").SupportedDataTypes);
        var modbus = catalog.Get("PLC.ModBusMaster").Descriptor;
        var cache = modbus.Methods.Single(m => m.Name == "ReadFromCache");
        Assert.Contains(TagDataType.Boolean, cache.SupportedDataTypes);
        Assert.Contains(TagDataType.Bit, cache.SupportedDataTypes);
        Assert.Contains(TagDataType.UInt64, cache.SupportedDataTypes);
        Assert.Equal(new[] { "Coil", "HoldingRegisters" }, modbus.Methods.Where(m => m.SupportsWrite).Select(m => m.Name).Order().ToArray());
        Assert.False(modbus.Methods.Single(m => m.Name == "Read").IsDiagnostic);
        foreach (var id in new[] { "OPC.DaClient", "OPC.UaClient" })
        {
            var methods = catalog.Get(id).Descriptor.Methods;
            Assert.Equal(methods.Single(m => m.Name == "ReadNode").SupportedDataTypes, methods.Single(m => m.Name == "Read").SupportedDataTypes);
        }
        Assert.All(catalog.Factories.Where(f => f.Descriptor.Id is not ("PLC.ModBusMaster" or "PLC.SiemensS7")),
            factory => Assert.False(factory.Descriptor.SupportsWrite));
    }

    [Fact]
    public void AllTwentySixNativeTypesAndFiveExplicitEndiansHaveLosslessMappings()
    {
        var types = Enum.GetValues<TagDataType>();
        Assert.Equal(26, types.Length);
        Assert.Equal(Enum.GetValues<DataTypeEnum>().Order(), types.Select(LegacyTypeMapping.ToLegacy).Order());
        Assert.Equal(26, types.Select(LegacyTypeMapping.ToLegacy).Distinct().Count());
        foreach (var endian in Enum.GetValues<ProtocolEndian>().Where(e => e != ProtocolEndian.FromByteOrder))
            Assert.Equal(endian.ToString(), LegacyTypeMapping.ToLegacy(new TagConfiguration { Endian = endian }).ToString());
        Assert.Equal(EndianEnum.LittleEndianSwap, LegacyTypeMapping.ToLegacy(new TagConfiguration { ByteOrder = RegisterByteOrder.CDAB }));
        Assert.Equal(ulong.MaxValue, LegacyTypeMapping.NormalizeValue(ulong.MaxValue));
    }

    [Fact]
    public void ScalarAndStructuredJsonNormalizationDoesNotLosePrecisionOrLifetime()
    {
        var normalized = Assert.IsType<JsonElement>(LegacyTypeMapping.NormalizeValue(
            Newtonsoft.Json.Linq.JObject.Parse("{\"counter\":18446744073709551615,\"samples\":[1.25,2.5]}")));
        Assert.Equal(ulong.MaxValue, normalized.GetProperty("counter").GetUInt64());
        Assert.Equal(2.5, normalized.GetProperty("samples")[1].GetDouble());
        Assert.Equal("  工件  ", LegacyTypeMapping.NormalizeValue("  工件  "));
        Assert.Null(LegacyTypeMapping.NormalizeValue(null));
        Assert.Null(LegacyTypeMapping.NormalizeValue(Newtonsoft.Json.Linq.JValue.CreateNull()));
        Assert.Null(LegacyTypeMapping.NormalizeValue(JsonSerializer.SerializeToElement<object?>(null)));
        Assert.Null(LegacyTypeMapping.NormalizeValue(DBNull.Value));
        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(LegacyTypeMapping.NormalizeValue(JsonSerializer.SerializeToElement(ulong.MaxValue))));
        var native = Assert.IsType<JsonElement>(LegacyTypeMapping.NormalizeValue(new NativeStructure()));
        Assert.Equal(ulong.MaxValue, native.GetProperty("Counter").GetUInt64());
        Assert.Equal(42, native.GetProperty("Data")[0].GetInt32());
    }

    public sealed class NativeStructure { public ulong Counter = ulong.MaxValue; public int[] Data = [42]; }

    [Fact]
    public async Task ShippedAllDriverConfigurationImportsAndValidatesWithoutHardwareConnections()
    {
        var configuration = await new JsonConfigurationStore(Path.Combine(AppContext.BaseDirectory, "migrated-drivers.json")).LoadAsync();
        Assert.Equal(12, configuration.Devices.Count);
        Assert.All(configuration.Devices, d => Assert.False(d.Enabled));
        Assert.False(configuration.Mqtt.Enabled);
        Assert.Empty(ConfigurationValidator.Validate(configuration, new DriverRegistry(catalog.Factories, new GatewayLog())));
    }

    [Theory]
    [InlineData("rTu")]
    [InlineData("ascii")]
    public void ImportedLegacyAndNativeSerialDevicesCannotBypassPortExclusivity(string mode)
    {
        var configuration = new GatewayConfiguration
        {
            Devices =
            [
                new DeviceConfiguration { DriverId = "modbus-rtu", SerialPortName = "COM1" },
                new DeviceConfiguration
                {
                    DriverId = "plc.modbusmaster",
                    Properties = new Dictionary<string, string> { ["MasterType"] = mode, ["PortName"] = "\\\\.\\com1" }
                }
            ]
        };
        var registry = new DriverRegistry(catalog.Factories.Append(new ModbusRtuDriverFactory()), new GatewayLog());
        Assert.Contains(ConfigurationValidator.Validate(configuration, registry), error => error.Contains("同一串口"));
        configuration.Devices[1].Properties["MasterType"] = "2";
        Assert.Contains(ConfigurationValidator.Validate(configuration, registry), error => error.Contains("MasterType") && error.Contains("格式错误"));
    }
}
