using System.Globalization;
using System.Reflection;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PluginInterface;
using static EdgeGateway.IotGateway.IotGatewayDriverCatalog;

namespace EdgeGateway.IotGateway;

internal sealed class IotGatewayDriverFactory : IDriverFactory
{
    private readonly Type _type;
    private readonly DriverDefinition _definition;
    private readonly IGatewayLog _log;
    private readonly PropertyInfo[] _parameters;
    private readonly Dictionary<string, MethodInfo> _methods;
    public DriverDescriptor Descriptor { get; }

    public IotGatewayDriverFactory(Type type, DriverDefinition definition, IGatewayLog log)
    {
        if (!typeof(IDriver).IsAssignableFrom(type)) throw new ArgumentException("类型未实现原 IDriver 契约。");
        _type = type;
        _definition = definition;
        _log = log;
        _parameters = type.GetProperties().Where(p => p.GetCustomAttribute<ConfigParameterAttribute>() is not null).ToArray();
        _methods = type.GetMethods().Where(m => m.GetCustomAttribute<MethodAttribute>() is not null &&
            m.ReturnType == typeof(DriverReturnValueModel) && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(DriverAddressIoArgModel))
            .ToDictionary(m => m.Name, StringComparer.Ordinal);
        var defaults = ReadDefaults();
        var methods = _methods.Values.Select(m => BuildMethod(m, definition)).OrderBy(m => m.IsDiagnostic).ThenBy(m => m.Name).ToArray();
        Descriptor = new DriverDescriptor(definition.Id, definition.Name,
            $"迁移自 iioter/iotgateway 的 {definition.Id}，通过原协议实现执行。", methods.Any(m => m.SupportsWrite))
        {
            IsLegacy = true,
            DefaultReadMethod = definition.DefaultMethod,
            Prerequisites = definition.Prerequisites,
            RequiresSta = definition.RequiresSta,
            Methods = methods,
            SupportedDataTypes = methods.SelectMany(m => m.SupportedDataTypes).Distinct().ToArray(),
            Parameters = _parameters.Select(property => new DriverParameterDescriptor(property.Name,
                property.GetCustomAttribute<ConfigParameterAttribute>()!.Description,
                ParameterDescription(property.Name, definition), property.PropertyType.Name,
                defaults.GetValueOrDefault(property.Name, ""),
                property.PropertyType.IsEnum ? Enum.GetNames(property.PropertyType) : [], property.Name == "DeviceId")
            {
                BoundProperty = property.Name switch { "DeviceId" => "Id", "Timeout" => "TimeoutMs", "MinPeriod" => "PollIntervalMs", _ => null }
            }).ToArray()
        };
        if (!_methods.ContainsKey(Descriptor.DefaultReadMethod)) throw new InvalidOperationException($"{definition.Id} 默认真实读取方法不存在。");
    }

    public IDeviceDriver Create(DeviceConfiguration configuration)
    {
        var errors = Validate(configuration);
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(configuration));
        return new IotGatewayDeviceDriver(configuration, this, _log);
    }

    public IReadOnlyList<string> Validate(DeviceConfiguration configuration)
    {
        var errors = new List<string>();
        if (configuration.Properties is null) return ["驱动参数 Properties 不能为 null。"];
        foreach (var key in configuration.Properties.Keys)
            if (!_parameters.Any(p => p.Name == key)) errors.Add($"未知驱动参数：{key}");
        foreach (var parameter in _parameters)
        {
            try
            {
                var value = ParameterValue(configuration, parameter);
                if ((parameter.Name is "Ip" or "IpAddress" or "DeviceIp" or "PortName" or "OpcServerName" or "Uri") && string.IsNullOrWhiteSpace(value?.ToString()))
                    errors.Add($"参数 {parameter.Name} 不能为空。");
                if (parameter.Name is "Port" or "DevicePort" && Convert.ToInt32(value) is < 1 or > 65535) errors.Add($"参数 {parameter.Name} 必须为 1–65535。");
                if (parameter.Name == "SlaveAddress" && Convert.ToInt32(value) is < 1 or > 247) errors.Add("Modbus 从站号必须为 1–247，不允许广播写入。");
                if (parameter.Name == "Uri" && value is string uri && !System.Uri.TryCreate(uri, UriKind.Absolute, out _)) errors.Add("URI 必须包含协议，例如 opc.tcp:// 或 http://。");
            }
            catch (Exception ex) { errors.Add($"参数 {parameter.Name} 格式错误：{ex.GetBaseException().Message}"); }
        }
        foreach (var tag in configuration.Tags ?? [])
        {
            if (tag is null) continue;
            var methodName = ResolveReadMethod(tag);
            var method = Descriptor.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method is null) { errors.Add($"点位 [{tag.Name}] 的读取方法 {methodName} 不存在。"); continue; }
            if (method.SupportedDataTypes.Count > 0 && !method.SupportedDataTypes.Contains(tag.DataType))
                errors.Add($"点位 [{tag.Name}] 方法 {method.Name} 不支持配置类型 {tag.DataType}。");
            if (!Enum.IsDefined(tag.Endian)) errors.Add($"点位 [{tag.Name}] 的协议字节序无效。");
            if (tag.Writable)
            {
                var writeName = string.IsNullOrWhiteSpace(tag.WriteMethod) ? methodName : tag.WriteMethod;
                if (!Descriptor.Methods.Any(m => m.Name == writeName && m.SupportsWrite)) errors.Add($"点位 [{tag.Name}] 方法 {writeName} 未实现写入。");
                if (!WriteTypes(writeName).Contains(tag.DataType)) errors.Add($"点位 [{tag.Name}] 的写入方法不支持类型 {tag.DataType}。");
            }
        }
        return errors;
    }

    internal IDriver CreateRaw(DeviceConfiguration configuration, ILogger logger)
    {
        var driver = (IDriver)Activator.CreateInstance(_type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, args: [configuration.Name, logger], culture: CultureInfo.InvariantCulture)!;
        try
        {
            foreach (var property in _parameters)
                if (property.CanWrite) property.SetValue(driver, ParameterValue(configuration, property));
            return driver;
        }
        catch { driver.Dispose(); throw; }
    }

    internal MethodInfo GetReadMethod(TagConfiguration tag) => _methods[ResolveReadMethod(tag)];
    internal string ResolveReadMethod(TagConfiguration tag) => string.IsNullOrWhiteSpace(tag.ReadMethod) ? Descriptor.DefaultReadMethod : tag.ReadMethod;
    internal string ResolveWriteMethod(TagConfiguration tag) => string.IsNullOrWhiteSpace(tag.WriteMethod) ? ResolveReadMethod(tag) : tag.WriteMethod;

    private object? ParameterValue(DeviceConfiguration configuration, PropertyInfo property)
    {
        if (property.Name == "DeviceId") return configuration.Id.ToString("D");
        if (property.Name == "Timeout") return Convert.ChangeType(configuration.TimeoutMs, property.PropertyType, CultureInfo.InvariantCulture);
        if (property.Name == "MinPeriod") return Convert.ChangeType(configuration.PollIntervalMs, property.PropertyType, CultureInfo.InvariantCulture);
        var descriptor = Descriptor.Parameters.First(p => p.Key == property.Name);
        var text = configuration.Properties.GetValueOrDefault(property.Name, descriptor.DefaultValue);
        return LegacyTypeMapping.ParseParameter(text, property.PropertyType);
    }

    private Dictionary<string, string> ReadDefaults()
    {
        IDriver? probe = null;
        var executor = new DedicatedDriverExecutor("driver-metadata-" + _definition.Id, () => probe?.Dispose(), _log);
        try
        {
            return executor.InvokeAsync(() =>
            {
                probe = (IDriver)Activator.CreateInstance(_type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, args: ["metadata", NullLogger.Instance], culture: CultureInfo.InvariantCulture)!;
                return _parameters.ToDictionary(p => p.Name, p => Convert.ToString(p.GetValue(probe), CultureInfo.InvariantCulture) ?? "");
            }, 3000, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { executor.StopAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static DriverMethodDescriptor BuildMethod(MethodInfo method, DriverDefinition definition)
    {
        var metadata = method.GetCustomAttribute<MethodAttribute>()!;
        var diagnostic = metadata.Name.Contains("测试", StringComparison.Ordinal) || metadata.Name.Contains("样例", StringComparison.Ordinal);
        var canWrite = definition.Id == "PLC.SiemensS7" && method.Name is "Read" or "ReadByteString" ||
            definition.Id == "PLC.ModBusMaster" && method.Name is "Coil" or "HoldingRegisters";
        var types = MethodTypes(definition.Id, method.Name);
        return new DriverMethodDescriptor(method.Name, metadata.Name + (diagnostic ? "（诊断示例）" : ""), metadata.Description, canWrite)
        {
            IsDiagnostic = diagnostic,
            SupportedDataTypes = types,
            DefaultDataType = DefaultType(definition.Id, method.Name, types),
            DefaultAddress = DefaultAddress(definition.Id, method.Name)
        };
    }

    private static TagDataType[] MethodTypes(string id, string method) => (id, method) switch
    {
        ("PLC.ModBusMaster", "Coil" or "Input") => [TagDataType.Boolean, TagDataType.Bit],
        ("PLC.ModBusMaster", "ReadMultiple") => [TagDataType.Any],
        ("PLC.ModBusMaster", "ReadFromCache") => [TagDataType.Boolean, TagDataType.Bit, TagDataType.UInt16, TagDataType.Int16, TagDataType.Bcd16, TagDataType.UInt32, TagDataType.Int32, TagDataType.Float32, TagDataType.Bcd32, TagDataType.UInt64, TagDataType.Int64, TagDataType.Float64, TagDataType.AsciiString],
        ("PLC.ModBusMaster", _) => [TagDataType.UInt16, TagDataType.Int16, TagDataType.Bcd16, TagDataType.UInt32, TagDataType.Int32, TagDataType.Float32, TagDataType.Bcd32, TagDataType.UInt64, TagDataType.Int64, TagDataType.Float64, TagDataType.AsciiString],
        ("PLC.SiemensS7", "ReadByteString") => [TagDataType.AsciiString],
        ("PLC.SiemensS7", _) => [TagDataType.Boolean, TagDataType.Bit, TagDataType.UInt8, TagDataType.Int8, TagDataType.UInt16, TagDataType.Int16, TagDataType.UInt32, TagDataType.Int32, TagDataType.Float32, TagDataType.AsciiString, TagDataType.Utf8String, TagDataType.Gb2312String, TagDataType.Any],
        ("PLC.MelsecMc" or "PLC.OmronFins" or "PLC.AllenBradley", _) => [TagDataType.Boolean, TagDataType.Bit, TagDataType.UInt8, TagDataType.Int8, TagDataType.UInt16, TagDataType.Int16, TagDataType.UInt32, TagDataType.Int32, TagDataType.Float32, TagDataType.Float64, TagDataType.UInt64, TagDataType.Int64, TagDataType.AsciiString, TagDataType.Utf8String],
        ("Mock.TcpClient", _) => [TagDataType.UInt8, TagDataType.Int8, TagDataType.Int16, TagDataType.Float32],
        ("Other.Toledo", _) => [TagDataType.UInt8, TagDataType.Int8, TagDataType.Int16, TagDataType.Int32, TagDataType.Float32, TagDataType.AsciiString, TagDataType.Custom1],
        ("OPC.DaClient", _) => [TagDataType.Boolean, TagDataType.Bit, TagDataType.UInt8, TagDataType.Int8, TagDataType.UInt16, TagDataType.Int16, TagDataType.UInt32, TagDataType.Int32, TagDataType.Float32, TagDataType.Float64, TagDataType.AsciiString, TagDataType.Utf8String, TagDataType.Any],
        _ => Enum.GetValues<TagDataType>()
    };

    private TagDataType[] WriteTypes(string method) => (_definition.Id, method) switch
    {
        ("PLC.ModBusMaster", "Coil") => [TagDataType.Boolean, TagDataType.Bit],
        ("PLC.ModBusMaster", "HoldingRegisters") => [TagDataType.UInt16, TagDataType.Int16, TagDataType.UInt32, TagDataType.Int32, TagDataType.Float32, TagDataType.UInt64, TagDataType.Int64, TagDataType.Float64, TagDataType.AsciiString],
        ("PLC.SiemensS7", "ReadByteString") => [TagDataType.AsciiString],
        ("PLC.SiemensS7", "Read") => [TagDataType.Bit, TagDataType.Boolean, TagDataType.UInt8, TagDataType.Int8, TagDataType.UInt16, TagDataType.Int16, TagDataType.UInt32, TagDataType.Int32, TagDataType.Float32, TagDataType.AsciiString],
        _ => []
    };

    private static TagDataType DefaultType(string id, string method, TagDataType[] types) =>
        method == "ReadByteString" ? TagDataType.AsciiString :
        id == "Mock.TcpClient" ? TagDataType.UInt8 : id == "Other.Toledo" ? TagDataType.Custom1 :
        id.StartsWith("PLC.", StringComparison.Ordinal) && types.Contains(TagDataType.UInt16) ? TagDataType.UInt16 :
        types.Contains(TagDataType.Any) ? TagDataType.Any : types[0];

    private static string DefaultAddress(string id, string method) => (id, method) switch
    {
        ("PLC.ModBusMaster", "ReadMultiple") => "3,0,10,cache1",
        ("PLC.ModBusMaster", "ReadFromCache") => "cache1,0",
        ("PLC.ModBusMaster", _) => "0",
        ("PLC.SiemensS7", "ReadByteString") => "DB1.DBB0,16",
        ("PLC.SiemensS7", _) => "DB1.DBW0",
        ("PLC.MelsecMc", "ReadMultiple") => "D,0,1,10",
        ("PLC.MelsecMc", _) => "D0",
        ("PLC.OmronFins", _) => "D0",
        ("PLC.AllenBradley", _) => "TagName",
        ("OPC.UaClient", _) => "ns=2;s=TagName",
        ("OPC.DaClient", _) => "TagName",
        ("CNC.MTConnect", _) => "dataItemId",
        ("Mock.TcpClient", _) => "1",
        _ => "0"
    };

    private static string ParameterDescription(string key, DriverDefinition definition) => key switch
    {
        "DeviceId" => "自动绑定设备唯一标识，不重复保存。",
        "Timeout" => "绑定基本信息中的请求超时（ms）；原协议单次操作使用此值。",
        "MinPeriod" => "绑定基本信息中的采集周期（ms）；每轮采样结束后等待此周期。",
        "AuthorizationCodeEnvironmentVariable" => "填写授权码环境变量名称，程序不保存或展示授权码。",
        "DataBits" when definition.Id == "Other.Toledo" => "原驱动将此参数用于 TCP 帧字节长度，默认 17；不是串口数据位。",
        "Parity" or "StopBits" or "ConnectionType" when definition.Id == "Other.Toledo" => "保留原配置元数据；当前托利多驱动使用 TCP，此字段不启用串口连接。",
        _ => "原驱动参数：" + key
    };
}
