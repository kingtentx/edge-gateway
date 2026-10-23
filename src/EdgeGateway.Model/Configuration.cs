using System.ComponentModel;

namespace EdgeGateway.Model;

public sealed class GatewayConfiguration
{
    public int SchemaVersion { get; set; } = 2;
    public string GatewayId { get; set; } = "edge-001";
    public string Name { get; set; } = "边缘采集网关";
    public bool AutoStart { get; set; }
    public int HistoryRetentionDays { get; set; } = 30;
    public int MaxHistoryRows { get; set; } = 1_000_000;
    public int MaxOutboxMessages { get; set; } = 50_000;
    public int PublishIntervalMs { get; set; } = 5_000;
    public TelemetryReportMode ReportMode { get; set; } = TelemetryReportMode.Periodic;
    public int HeartbeatIntervalMs { get; set; } = 60_000;
    public MqttConfiguration Mqtt { get; set; } = new();
    public List<ChannelConfiguration> Channels { get; set; } = [];
    public List<DeviceConfiguration> Devices { get; set; } = [];
}

public sealed class MqttConfiguration
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public bool UseTls { get; set; }
    public string ClientId { get; set; } = "edge-001";
    public string Username { get; set; } = "";
    // Store only an environment-variable name, never a broker password.
    public string PasswordEnvironmentVariable { get; set; } = "EDGE_MQTT_PASSWORD";
    public string Topic { get; set; } = "edge/{gatewayId}/telemetry";
    public int TimeoutMs { get; set; } = 10_000;
    public MqttPlatform Platform { get; set; } = MqttPlatform.Generic;
    public bool RpcEnabled { get; set; }
    public bool AllowRemoteWrite { get; set; }
    public string RpcRequestTopic { get; set; } = "edge/{gatewayId}/rpc/request";
    public string RpcResponseTopic { get; set; } = "edge/{gatewayId}/rpc/response";
    public int RpcTimeoutMs { get; set; } = 10_000;
    public string UsernameEnvironmentVariable { get; set; } = "";
    public string PlatformDeviceType { get; set; } = "default";
    public string ThingsCloudTypeKey { get; set; } = "";
    public string PlatformGatewayId { get; set; } = "";
}

public sealed class DeviceConfiguration
{
    [Browsable(false)] public Guid Id { get; set; } = Guid.NewGuid();
    [Category("基本信息"), DisplayName("设备名称")] public string Name { get; set; } = "新设备";
    [Category("基本信息"), DisplayName("分组")] public string Group { get; set; } = "默认分组";
    [Browsable(false)] public Guid? ChannelId { get; set; }
    [Category("基本信息"), DisplayName("驱动标识")] public string DriverId { get; set; } = "simulator";
    [Category("基本信息"), DisplayName("启用采集")] public bool Enabled { get; set; } = true;
    [Category("采集参数"), DisplayName("周期 (ms)")] public int PollIntervalMs { get; set; } = 1000;
    [Category("采集参数"), DisplayName("超时 (ms)")] public int TimeoutMs { get; set; } = 3000;
    [Category("采集参数"), DisplayName("扫描模式")] public DeviceScanMode ScanMode { get; set; } = DeviceScanMode.DeviceRate;
    [Category("采集参数"), DisplayName("故障自动降速")] public bool AutoDemotionEnabled { get; set; }
    [Category("采集参数"), DisplayName("降速失败次数")] public int DemotionFailureCount { get; set; } = 3;
    [Category("采集参数"), DisplayName("降速重试间隔 (ms)")] public int DemotionIntervalMs { get; set; } = 30_000;
    [Category("网络连接"), DisplayName("主机地址")] public string Host { get; set; } = "127.0.0.1";
    [Category("网络连接"), DisplayName("TCP 端口")] public int Port { get; set; } = 502;
    [Category("Modbus"), DisplayName("站号 (1–247)")] public int UnitId { get; set; } = 1;
    [Category("串口连接"), DisplayName("串口名称")] public string SerialPortName { get; set; } = "COM1";
    [Category("串口连接"), DisplayName("波特率")] public int BaudRate { get; set; } = 9600;
    [Category("串口连接"), DisplayName("数据位")] public int DataBits { get; set; } = 8;
    [Category("串口连接"), DisplayName("校验位")] public string Parity { get; set; } = "None";
    [Category("串口连接"), DisplayName("停止位")] public string StopBits { get; set; } = "One";
    [Browsable(false)] public List<TagConfiguration> Tags { get; set; } = [];
    [Browsable(false)] public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class TagConfiguration
{
    [Browsable(false)] public Guid Id { get; set; } = Guid.NewGuid();
    [DisplayName("点位名称")] public string Name { get; set; } = "新点位";
    [DisplayName("地址"), Description("Modbus 使用从 0 开始的地址；模拟驱动支持 sine、ramp、random、constant:数值、bool。")] public string Address { get; set; } = "0";
    [DisplayName("数据类型")] public TagDataType DataType { get; set; } = TagDataType.UInt16;
    [DisplayName("寄存区")] public ModbusArea Area { get; set; } = ModbusArea.HoldingRegister;
    [DisplayName("字节序")] public RegisterByteOrder ByteOrder { get; set; } = RegisterByteOrder.ABCD;
    [DisplayName("读取方法")] public string ReadMethod { get; set; } = "";
    [DisplayName("写入方法")] public string WriteMethod { get; set; } = "";
    [DisplayName("协议字节序")] public ProtocolEndian Endian { get; set; } = ProtocolEndian.FromByteOrder;
    [DisplayName("启用")] public bool Enabled { get; set; } = true;
    [DisplayName("上传平台"), Description("关闭后仍采集、显示并保存历史，但新生成的 MQTT 消息不包含此点位；已入队消息继续补传。")] public bool UploadEnabled { get; set; } = true;
    [DisplayName("允许写入")] public bool Writable { get; set; }
    [DisplayName("倍率")] public double Scale { get; set; } = 1;
    [DisplayName("偏移量")] public double Offset { get; set; }
    [DisplayName("计算表达式")] public string Expression { get; set; } = "";
    [DisplayName("扫描周期 (ms)"), Description("0 表示继承设备周期；按点位周期扫描时生效。")]
    public int ScanIntervalMs { get; set; }
    [DisplayName("上报死区"), Description("工程值相对上次成功入队值的绝对变化量；0 为任意变化。")]
    public double Deadband { get; set; }
    [DisplayName("单位")] public string Unit { get; set; } = "";
    [DisplayName("报警下限")] public double? LowLimit { get; set; }
    [DisplayName("报警上限")] public double? HighLimit { get; set; }
    [DisplayName("说明")] public string Description { get; set; } = "";
}

// Preserve the first seven numeric values for existing schema-v1 configuration files.
public enum TagDataType
{
    Boolean, Int16, UInt16, Int32, UInt32, Float32, Float64,
    Bit, UInt8, Int8, Bcd16, Bcd32, UInt64, Int64, AsciiString, Utf8String,
    DateTime, TimeStampMs, TimeStampS, Any, Custom1, Custom2, Custom3, Custom4, Custom5, Gb2312String
}
public enum ProtocolEndian { FromByteOrder, None, BigEndian, LittleEndian, BigEndianSwap, LittleEndianSwap }
public enum ModbusArea { Coil, DiscreteInput, HoldingRegister, InputRegister }
public enum RegisterByteOrder { ABCD, BADC, CDAB, DCBA }
public enum SampleQuality { Good, Bad, Uncertain }
public enum DeviceState { Stopped, Connecting, Running, Reconnecting, Faulted }
public enum GatewayLogLevel { Debug, Information, Warning, Error }
