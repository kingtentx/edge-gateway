using System.Globalization;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public static class DemoConfiguration
{
    public static GatewayConfiguration Create() => new()
    {
        Devices =
        [
            new DeviceConfiguration
            {
                Name = "演示机组", Group = "模拟设备", DriverId = "simulator", PollIntervalMs = 1000,
                Tags =
                [
                    new TagConfiguration { Name = "出水温度", Address = "sine", DataType = TagDataType.Float64, Scale = 0.5, Offset = 15, Unit = "°C", HighLimit = 48, LowLimit = 32 },
                    new TagConfiguration { Name = "运行频率", Address = "ramp", DataType = TagDataType.Float64, Unit = "Hz" },
                    new TagConfiguration { Name = "运行状态", Address = "bool", DataType = TagDataType.Boolean },
                    new TagConfiguration { Name = "目标温度", Address = "constant:45", DataType = TagDataType.Float64, Unit = "°C", Writable = true }
                ]
            },
            new DeviceConfiguration
            {
                Name = "Modbus TCP 示例（需配置）", Group = "现场设备", DriverId = "modbus-tcp", Enabled = false,
                Host = "127.0.0.1", Port = 502, UnitId = 1,
                Tags = [new TagConfiguration { Name = "保持寄存器 0", Address = "0", DataType = TagDataType.UInt16 }]
            },
            CreateModbusRtuExample()
        ]
    };

    public static DeviceConfiguration CreateModbusRtuExample()
    {
        // 与导入示例使用相同的固定标识，重复导入时可检测冲突。
        var device = new DeviceConfiguration
        {
            Id = Guid.Parse("ac88ef37-7062-48d6-a13e-6f16fedf4438"),
            Name = "Modbus RTU 从站 1（COM11）",
            Group = "串口联调",
            DriverId = "modbus-rtu",
            Enabled = false,
            PollIntervalMs = 1000,
            TimeoutMs = 3000,
            UnitId = 1,
            SerialPortName = "COM11",
            BaudRate = 9600,
            DataBits = 8,
            Parity = "None",
            StopBits = "One"
        };

        for (var address = 0; address < 10; address++)
        {
            var registerAddress = address.ToString(CultureInfo.InvariantCulture);
            device.Tags.Add(new TagConfiguration
            {
                Id = Guid.Parse("a957a450-ea11-4cfb-9007-" + address.ToString("D12", CultureInfo.InvariantCulture)),
                Name = $"保持寄存器 {registerAddress}",
                Address = registerAddress,
                DataType = TagDataType.UInt16,
                Area = ModbusArea.HoldingRegister,
                ByteOrder = RegisterByteOrder.ABCD,
                Enabled = true,
                Writable = false
            });
        }

        return device;
    }
}
