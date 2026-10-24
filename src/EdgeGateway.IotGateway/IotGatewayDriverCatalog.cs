using System.Reflection;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.IotGateway;

/// <summary>All twelve original driver projects, registered through their real IDriver implementations.</summary>
public static class IotGatewayDriverCatalog
{
    internal static readonly DriverDefinition[] Definitions =
    [
        new("PLC.AllenBradley", "PLC.AllenBradley.DeviceAllenBradley", "Allen-Bradley PLC", "Read", "需可访问的 EtherNet/IP PLC 与正确标签地址。"),
        new("PLC.MelsecMc", "PLC.MelsecMc.DeviceMelsecMc", "三菱 MC PLC", "Read", "支持原 A_1E / Qna_3E 参数及批量缓存方法。"),
        new("PLC.ModBusMaster", "PLC.ModBusMaster.DeviceModBusMaster", "Modbus 主站（原版全模式）", "HoldingRegisters", "按 MasterType 配置 TCP/UDP/RTU/ASCII 及透传模式。串口设备需独占端口。"),
        new("PLC.OmronFins", "PLC.OmronFins.DeviceOmronFins", "欧姆龙 FINS PLC", "Read", "需配置设备端 FINS 网络与地址。"),
        new("PLC.SiemensS7", "PLC.SiemensS7.DeviceSiemensS7", "西门子 S7 PLC", "Read", "需配置 CPU、Rack、Slot，以及 PLC 端访问授权；DB 地址按设备设置。"),
        new("OPC.DaClient", "OPC.DaClient.DeviceDaClient", "OPC DA 客户端", "ReadNode", "仅 Windows；需注册匹配位数的 OPC Automation、实际 OPC DA Server，并配置 DCOM 权限。", true),
        new("OPC.UaClient", "OPC.UaClient.DeviceUaClient", "OPC UA 客户端", "ReadNode", "需实际 OPC UA Endpoint、有效 NodeId；证书信任及安全配置遵循原驱动实现。"),
        new("CNC.Fanuc", "CNC.Fanuc.DeviceFanuc", "FANUC FOCAS", "ReadDeviceType", "仅 Windows x64；需要 FANUC 官方 FWLIB64.dll 及其配套依赖、设备以太网 FOCAS 功能。"),
        new("CNC.Fanuc.H", "CNC.Fanuc.H.DeviceFanuc", "FANUC HslCommunication", "ReadSysStatusInfo", "需要配置有效 HslCommunication 授权码及受支持 FANUC 设备。"),
        new("CNC.MTConnect", "CNC.MTConnect.DeviceMTClient", "MTConnect 客户端", "ReadById", "需要实际 MTConnect HTTP Agent URI 和 dataItemId。"),
        new("Mock.TcpClient", "Mock.TcpClient.DeviceTcpClient", "原始 TCP 示例协议", "Read", "真实 TCP 收发原示例协议；请求 01 02 03 04，响应 8 字节且首字节 08，不生成本地模拟数值。"),
        new("Other.Toledo", "Other.Toledo.DeviceToledo", "托利多称重 TCP", "Read", "原驱动通过 TCP 读取称重帧；DataBits 为期望帧长度，串口外观参数不代表已实现串口通讯。")
    ];

    public static IReadOnlyList<IDriverFactory> CreateFactories(IGatewayLog log) => Definitions.Select(definition =>
    {
        var assembly = Assembly.Load(new AssemblyName(definition.Id));
        var type = assembly.GetType(definition.TypeName, throwOnError: true)!;
        return (IDriverFactory)new IotGatewayDriverFactory(type, definition, log);
    }).ToArray();

    internal sealed record DriverDefinition(string Id, string TypeName, string Name, string DefaultMethod,
        string Prerequisites, bool RequiresSta = false);
}
