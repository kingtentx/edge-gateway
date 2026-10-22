using System.Globalization;
using System.IO.Ports;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Driver.Modbus;

public sealed class ModbusTcpDriverFactory : IDriverFactory
{
    public DriverDescriptor Descriptor { get; } = new("modbus-tcp", "Modbus TCP", "FC01/02/03/04 采集，FC05/06/16 写入；0 基址", true);
    public IDeviceDriver Create(DeviceConfiguration configuration)
    {
        ModbusValidation.ThrowIfInvalid(Validate(configuration));
        return new ModbusDriver(configuration, new ModbusTcpTransport(configuration));
    }
    public IReadOnlyList<string> Validate(DeviceConfiguration configuration)
    {
        var errors = ModbusValidation.Validate(configuration);
        if (string.IsNullOrWhiteSpace(configuration.Host)) errors.Add("Modbus TCP 主机地址不能为空。");
        if (configuration.Port is < 1 or > 65535) errors.Add("Modbus TCP 端口应为 1–65535。");
        return errors;
    }
}

public sealed class ModbusRtuDriverFactory : IDriverFactory
{
    public DriverDescriptor Descriptor { get; } = new("modbus-rtu", "Modbus RTU", "串口 RTU / CRC16；首版同一串口仅允许配置一个设备", true);
    public IDeviceDriver Create(DeviceConfiguration configuration)
    {
        ModbusValidation.ThrowIfInvalid(Validate(configuration));
        return new ModbusDriver(configuration, new ModbusRtuTransport(configuration));
    }
    public IReadOnlyList<string> Validate(DeviceConfiguration configuration)
    {
        var errors = ModbusValidation.Validate(configuration);
        if (string.IsNullOrWhiteSpace(configuration.SerialPortName)) errors.Add("Modbus RTU 串口名称不能为空。");
        if (configuration.BaudRate is < 300 or > 4_000_000) errors.Add("Modbus RTU 波特率应为 300–4000000。");
        if (configuration.DataBits != 8) errors.Add("Modbus RTU 二进制报文要求 8 个数据位。");
        if (!Enum.TryParse<Parity>(configuration.Parity, true, out var parity) || !Enum.IsDefined(parity))
            errors.Add("串口校验位应为 None、Odd、Even、Mark 或 Space。");
        if (!Enum.TryParse<StopBits>(configuration.StopBits, true, out var stop) || stop is not (StopBits.One or StopBits.Two))
            errors.Add("串口停止位应为 One 或 Two。");
        return errors;
    }
}

internal static class ModbusValidation
{
    internal static List<string> Validate(DeviceConfiguration configuration)
    {
        var errors = new List<string>();
        if (configuration.UnitId is < 1 or > 247) errors.Add("Modbus 站号应为 1–247；不支持广播读写。");
        if (configuration.TimeoutMs is < 100 or > 120_000) errors.Add("Modbus 超时应为 100–120000 ms。");
        foreach (var tag in configuration.Tags.Where(t => t.Enabled))
        {
            try { ValidateTag(tag); }
            catch (ArgumentException ex) { errors.Add($"点位“{tag.Name}”：{ex.Message}"); }
        }
        return errors;
    }

    internal static void ThrowIfInvalid(IReadOnlyList<string> errors)
    {
        if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
    }

    internal static ushort ValidateTag(TagConfiguration tag)
    {
        if (!Enum.IsDefined(tag.Area) || !Enum.IsDefined(tag.DataType) || !Enum.IsDefined(tag.ByteOrder))
            throw new ArgumentException("寄存区、数据类型或字节序无效。");
        if (!ushort.TryParse(tag.Address, NumberStyles.None, CultureInfo.InvariantCulture, out var address))
            throw new ArgumentException("地址必须是 0–65535 的十进制偏移量（从 0 开始，不填写 40001 等手册前缀）。");
        if (tag.Area is ModbusArea.Coil or ModbusArea.DiscreteInput && tag.DataType != TagDataType.Boolean)
            throw new ArgumentException("线圈和离散输入只能使用 Boolean 数据类型。");
        var count = tag.Area is ModbusArea.Coil or ModbusArea.DiscreteInput ? 1 : RegisterCodec.GetRegisterCount(tag.DataType);
        if ((int)address + count > 65536) throw new ArgumentException("多寄存器点位地址越过 65535 边界。");
        if (tag.Writable && tag.Area is ModbusArea.DiscreteInput or ModbusArea.InputRegister)
            throw new ArgumentException("离散输入和输入寄存器只读，不能启用写入。");
        return address;
    }
}
