using System.Text;
using System.Text.RegularExpressions;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public static class ConfigurationValidator
{
    public static IReadOnlyList<string> Validate(GatewayConfiguration configuration, IDriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        errors.AddRange(OpcUaConfigurationValidator.Validate(configuration.OpcUa));
        if (configuration.SchemaVersion != 2) errors.Add("仅支持配置格式版本 2；请通过配置存储加载并迁移版本 1。");
        if (string.IsNullOrWhiteSpace(configuration.GatewayId) || !Regex.IsMatch(configuration.GatewayId, "^[A-Za-z0-9._-]{1,100}$"))
            errors.Add("网关标识须为 1–100 位字母、数字、点、下划线或短横线。");
        if (string.IsNullOrWhiteSpace(configuration.Name)) errors.Add("网关名称不能为空。");
        if (configuration.PublishIntervalMs is < 100 or > 3_600_000) errors.Add("上报周期必须为 100–3600000 ms。");
        if (!Enum.IsDefined(configuration.ReportMode)) errors.Add("不支持的上报模式。");
        if (configuration.HeartbeatIntervalMs is < 100 or > 86_400_000) errors.Add("心跳周期必须为 100–86400000 ms。");
        if (configuration.ReportMode == TelemetryReportMode.OnChangeWithHeartbeat && configuration.HeartbeatIntervalMs < configuration.PublishIntervalMs)
            errors.Add("心跳周期不能小于上报检查周期。");
        if (configuration.HistoryRetentionDays is < 1 or > 3650) errors.Add("历史保留时间必须为 1–3650 天。");
        if (configuration.MaxHistoryRows is < 100 or > 10_000_000) errors.Add("历史最大记录数必须为 100–10000000。");
        if (configuration.MaxOutboxMessages is < 1 or > 1_000_000) errors.Add("补传队列容量必须为 1–1000000。");
        var mqtt = configuration.Mqtt;
        if (mqtt is null) errors.Add("MQTT 配置缺失。");
        else if (mqtt.Enabled)
        {
            if (string.IsNullOrWhiteSpace(mqtt.Host) || mqtt.Host.Contains("://")) errors.Add("MQTT 主机应为 IP 或域名，不含协议前缀。");
            if (mqtt.Port is < 1 or > 65535) errors.Add("MQTT 端口必须为 1–65535。");
            if (string.IsNullOrWhiteSpace(mqtt.ClientId) || Encoding.UTF8.GetByteCount(mqtt.ClientId) > 1000) errors.Add("MQTT 客户端标识不能为空且不能超过 1000 字节。");
            if (string.IsNullOrWhiteSpace(mqtt.Topic) || mqtt.Topic.IndexOfAny(['+', '#', '\0']) >= 0 || Encoding.UTF8.GetByteCount(mqtt.Topic) > 65000)
                errors.Add("MQTT 发布主题不能为空，不能包含通配符 +、# 或空字符，长度不能超过 65000 字节。");
            if (mqtt.TimeoutMs is < 500 or > 120000) errors.Add("MQTT 超时必须为 500–120000 ms。");
            if (!string.IsNullOrEmpty(mqtt.PasswordEnvironmentVariable) && !Regex.IsMatch(mqtt.PasswordEnvironmentVariable, "^[A-Za-z_][A-Za-z0-9_]*$"))
                errors.Add("MQTT 密码只允许配置合法的环境变量名称。");
        }
        var channels = new HashSet<Guid>();
        var channelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (configuration.Channels is null) errors.Add("通道列表不能为 null。");
        else
        {
            if (configuration.Channels.Count > 500) errors.Add("最多配置 500 个通道。");
            foreach (var channel in configuration.Channels)
            {
                if (channel is null) { errors.Add("通道列表包含空对象。"); continue; }
                if (channel.Id == Guid.Empty || !channels.Add(channel.Id)) errors.Add("通道标识为空或重复。");
                if (string.IsNullOrWhiteSpace(channel.Name) || channel.Name.Length > 100 || !channelNames.Add(channel.Name.Trim()))
                    errors.Add("通道名称不能为空、超过 100 字符或重复。");
                if (channel.InterRequestDelayMs is < 0 or > 60_000) errors.Add($"通道 [{channel.Name}] 的请求间隔须为 0–60000 ms。");
            }
        }
        if (configuration.Devices is null) { errors.Add("设备列表不能为 null。"); return errors; }
        if (configuration.Devices.Count > 500) errors.Add("当前版本最多配置 500 台设备。");
        var deviceIds = new HashSet<Guid>();
        var tagIds = new HashSet<Guid>();
        var serialPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in configuration.Devices)
        {
            if (device is null) { errors.Add("设备列表包含空对象。"); continue; }
            var label = $"设备 [{device.Name}]";
            if (device.Id == Guid.Empty || !deviceIds.Add(device.Id)) errors.Add($"{label} 的标识为空或重复。");
            if (string.IsNullOrWhiteSpace(device.Name)) errors.Add("设备名称不能为空。");
            if (device.Group is null) errors.Add($"{label} 的分组不能为 null。");
            if (device.ChannelId is Guid channelId && !channels.Contains(channelId)) errors.Add($"{label} 引用了不存在的通信通道。");
            if (!Enum.IsDefined(device.ScanMode)) errors.Add($"{label} 的扫描模式无效。");
            if (device.DemotionFailureCount is < 1 or > 30) errors.Add($"{label} 的降速失败次数须为 1–30。");
            if (device.DemotionIntervalMs is < 100 or > 3_600_000) errors.Add($"{label} 的降速重试间隔须为 100–3600000 ms。");
            if (device.PollIntervalMs is < 100 or > 3_600_000) errors.Add($"{label} 的采集周期必须为 100–3600000 ms。");
            if (device.TimeoutMs is < 100 or > 120000) errors.Add($"{label} 的超时必须为 100–120000 ms。");
            IDriverFactory? factory = null;
            try { factory = registry.GetFactory(device.DriverId ?? ""); }
            catch (Exception ex) { errors.Add($"{label}：{ex.Message}"); }
            var canonicalDriverId = factory?.Descriptor.Id ?? device.DriverId;
            var masterType = device.Properties?.GetValueOrDefault("MasterType", "Tcp")?.Trim();
            var serialPort = string.Equals(canonicalDriverId, "modbus-rtu", StringComparison.OrdinalIgnoreCase) ? device.SerialPortName :
                string.Equals(canonicalDriverId, "PLC.ModBusMaster", StringComparison.OrdinalIgnoreCase) && device.Properties is not null &&
                (string.Equals(masterType, "Rtu", StringComparison.OrdinalIgnoreCase) || string.Equals(masterType, "Ascii", StringComparison.OrdinalIgnoreCase))
                    ? device.Properties.GetValueOrDefault("PortName", "COM1") : null;
            if (device.Enabled && serialPort is not null && !serialPorts.Add(NormalizeSerialPort(serialPort)))
                errors.Add($"{label} 与其他已启用设备使用同一串口；当前版本每个串口只支持一个启用设备。");
            if (device.Tags is null) { errors.Add($"{label} 的点位列表不能为 null。"); continue; }
            if (device.Tags.Count > 2000) errors.Add($"{label} 最多支持 2000 个点位。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in device.Tags)
            {
                if (tag is null) { errors.Add($"{label} 包含空点位。"); continue; }
                var tagLabel = $"{label} 点位 [{tag.Name}]";
                if (tag.Id == Guid.Empty || !tagIds.Add(tag.Id)) errors.Add($"{tagLabel} 的标识为空或重复。");
                if (string.IsNullOrWhiteSpace(tag.Name) || !names.Add(tag.Name)) errors.Add($"{tagLabel} 的名称为空或设备内重复。");
                if (!Enum.IsDefined(tag.DataType) || !Enum.IsDefined(tag.Area) || !Enum.IsDefined(tag.ByteOrder) || !Enum.IsDefined(tag.Endian)) errors.Add($"{tagLabel} 包含不支持的类型、寄存区或字节序。");
                if (!double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset)) errors.Add($"{tagLabel} 的倍率与偏移必须为有限数值。");
                if (tag.ScanIntervalMs != 0 && tag.ScanIntervalMs is < 100 or > 3_600_000)
                    errors.Add($"{tagLabel} 的扫描周期须为 0（继承设备）或 100–3600000 ms。");
                if (!double.IsFinite(tag.Deadband) || tag.Deadband < 0) errors.Add($"{tagLabel} 的上报死区须为非负有限数值。");
                if (tag.Deadband != 0 && !TagDataTypes.IsNumeric(tag.DataType)) errors.Add($"{tagLabel} 为非数值类型，不能配置数值死区。");
                if (!string.IsNullOrWhiteSpace(tag.Expression))
                {
                    if (!TagDataTypes.IsNumeric(tag.DataType) || tag.ReadMethod == "ReadMultiple") errors.Add($"{tagLabel} 的表达式仅支持单个数值。");
                    if (tag.Writable) errors.Add($"{tagLabel} 配置表达式后只能读取；请使用独立原始点位执行写入。");
                    if (NumericExpression.Validate(tag.Expression) is string expressionError) errors.Add($"{tagLabel} 的表达式无效：{expressionError}");
                }
                if (tag.Writable && tag.Scale == 0) errors.Add($"{tagLabel} 允许写入时倍率不能为 0。");
                if (tag.DataType == TagDataType.Boolean && (tag.Scale != 1 || tag.Offset != 0)) errors.Add($"{tagLabel} 为布尔类型，倍率必须为 1、偏移必须为 0。");
                if (!TagDataTypes.IsNumeric(tag.DataType) && (tag.Scale != 1 || tag.Offset != 0 || tag.HighLimit.HasValue || tag.LowLimit.HasValue))
                    errors.Add($"{tagLabel} 为非数值类型，倍率须为 1、偏移须为 0，且不能配置数值阈值。");
                if (tag.LowLimit is double low && !double.IsFinite(low) || tag.HighLimit is double high && !double.IsFinite(high)) errors.Add($"{tagLabel} 的报警阈值必须为有限数值。");
                if (tag.LowLimit.HasValue && tag.HighLimit.HasValue && tag.LowLimit > tag.HighLimit) errors.Add($"{tagLabel} 的报警下限不能高于上限。");
                if (tag.Writable && factory is not null && !factory.Descriptor.SupportsWrite) errors.Add($"{tagLabel} 所用驱动不支持写入。");
            }
            if (factory is not null)
            {
                try { errors.AddRange(factory.Validate(device).Select(e => $"{label}：{e}")); }
                catch (Exception ex) { errors.Add($"{label} 驱动验证失败：{ex.Message}"); }
            }
        }
        if (mqtt is not null) errors.AddRange(PlatformMqttAdapter.ValidateGateway(configuration));
        return errors;
    }

    private static string NormalizeSerialPort(string name)
    {
        var normalized = name.Trim();
        return normalized.StartsWith("\\\\.\\", StringComparison.Ordinal) ? normalized[4..] : normalized;
    }
}
