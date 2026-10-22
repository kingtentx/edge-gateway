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
            }
        ]
    };
}
