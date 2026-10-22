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
        if (configuration.SchemaVersion != 1) errors.Add("仅支持配置格式版本 1。");
        if (string.IsNullOrWhiteSpace(configuration.GatewayId) || !Regex.IsMatch(configuration.GatewayId, "^[A-Za-z0-9._-]{1,100}$"))
            errors.Add("网关标识须为 1–100 位字母、数字、点、下划线或短横线。");
        if (string.IsNullOrWhiteSpace(configuration.Name)) errors.Add("网关名称不能为空。");
        if (configuration.PublishIntervalMs is < 100 or > 3_600_000) errors.Add("上报周期必须为 100–3600000 ms。");
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
            if (device.PollIntervalMs is < 100 or > 3_600_000) errors.Add($"{label} 的采集周期必须为 100–3600000 ms。");
            if (device.TimeoutMs is < 100 or > 120000) errors.Add($"{label} 的超时必须为 100–120000 ms。");
            IDriverFactory? factory = null;
            try { factory = registry.GetFactory(device.DriverId ?? ""); }
            catch (Exception ex) { errors.Add($"{label}：{ex.Message}"); }
            if (device.Enabled && string.Equals(device.DriverId, "modbus-rtu", StringComparison.OrdinalIgnoreCase) &&
                !serialPorts.Add(device.SerialPortName?.Trim() ?? ""))
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
                if (!Enum.IsDefined(tag.DataType) || !Enum.IsDefined(tag.Area) || !Enum.IsDefined(tag.ByteOrder)) errors.Add($"{tagLabel} 包含不支持的类型、寄存区或字节序。");
                if (!double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset)) errors.Add($"{tagLabel} 的倍率与偏移必须为有限数值。");
                if (tag.Writable && tag.Scale == 0) errors.Add($"{tagLabel} 允许写入时倍率不能为 0。");
                if (tag.DataType == TagDataType.Boolean && (tag.Scale != 1 || tag.Offset != 0)) errors.Add($"{tagLabel} 为布尔类型，倍率必须为 1、偏移必须为 0。");
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
        return errors;
    }
}
