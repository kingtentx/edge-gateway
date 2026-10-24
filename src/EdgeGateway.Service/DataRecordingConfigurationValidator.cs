using System.Text.RegularExpressions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>Checks configuration without opening a database or performing DDL.</summary>
public static partial class DataRecordingConfigurationValidator
{
    public static IReadOnlyList<string> Validate(GatewayConfiguration configuration)
    {
        var errors = new List<string>();
        var logging = configuration.Logging;
        if (logging is null) errors.Add("日志配置缺失。");
        else
        {
            if (!Enum.IsDefined(logging.MinimumLevel)) errors.Add("日志等级无效。");
            if (logging.MaxFileSizeMb is < 1 or > 1024) errors.Add("单个日志文件大小须为 1–1024 MiB。");
            if (logging.RetainedFileCount is < 1 or > 1000) errors.Add("日志保留文件数须为 1–1000。");
            if (logging.RetainedDays is < 1 or > 3650) errors.Add("日志保留天数须为 1–3650。");
            if (logging.MinimumFreeDiskSpaceMb is < 16 or > 1048576) errors.Add("日志磁盘保护阈值须为 16–1048576 MiB。");
            if (logging.DiskResumeMarginMb is < 1 or > 65536) errors.Add("日志磁盘恢复余量须为 1–65536 MiB。");
        }
        if (configuration.Databases is null || configuration.DatabaseWriteGroups is null)
        {
            errors.Add("数据库连接与写入组列表不能为 null。");
            return errors;
        }
        if (configuration.Databases.Count > 32) errors.Add("最多配置 32 个外部数据库连接。");
        if (configuration.DatabaseWriteGroups.Count > 128) errors.Add("最多配置 128 个数据库写入组。");
        var connections = new Dictionary<Guid, DatabaseConnectionConfiguration>();
        var connectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in configuration.Databases)
        {
            if (connection is null) { errors.Add("数据库连接列表包含空对象。"); continue; }
            if (connection.Id == Guid.Empty || !connections.TryAdd(connection.Id, connection)) errors.Add("数据库连接标识为空或重复。");
            if (!ValidName(connection.Name) || !connectionNames.Add(connection.Name.Trim())) errors.Add("数据库连接名称须为 1–100 字符且不能重复。");
            errors.AddRange(ValidateConnection(connection));
        }
        var tags = (configuration.Devices ?? []).Where(device => device is not null)
            .SelectMany(device => device.Tags ?? []).Where(tag => tag is not null).Select(tag => tag.Id).ToHashSet();
        var groupIds = new HashSet<Guid>();
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in configuration.DatabaseWriteGroups)
        {
            if (group is null) { errors.Add("数据库写入组列表包含空对象。"); continue; }
            var label = $"写入组 [{group.Name}]";
            if (group.Id == Guid.Empty || !groupIds.Add(group.Id)) errors.Add($"{label} 标识为空或重复。");
            if (!ValidName(group.Name) || !groupNames.Add(group.Name.Trim())) errors.Add($"{label} 名称须为 1–100 字符且不能重复。");
            if (!connections.ContainsKey(group.ConnectionId)) errors.Add($"{label} 引用了不存在的数据库连接。");
            if (!Enum.IsDefined(group.Mode)) errors.Add($"{label} 存储模式无效。");
            if (!IsIdentifier(group.Table) || (!string.IsNullOrEmpty(group.Schema) && !IsIdentifier(group.Schema)))
                errors.Add($"{label} 的表名和 schema 须为字母/下划线开头的字母、数字或下划线，UTF-8 不超过 63 字节；请分别填写，不接受 SQL 表达式。");
            if (group.TagIds is null || group.Columns is null) { errors.Add($"{label} 点位或字段列表不能为 null。"); continue; }
            if (group.TagIds.Count != group.TagIds.Distinct().Count()) errors.Add($"{label} 包含重复点位。");
            foreach (var tagId in group.TagIds.Where(id => !tags.Contains(id))) errors.Add($"{label} 选择了不存在的点位 {tagId}，请先调整数据库映射再删除点位。");
            if (group.WriteIntervalMs is < 100 or > 3_600_000) errors.Add($"{label} 写入间隔须为 100–3600000 ms。");
            if (group.MaxSampleAgeMs is < 100 or > 86_400_000) errors.Add($"{label} 允许的数据年龄须为 100–86400000 ms。");
            if (group.Mode != DatabaseStorageMode.ColumnMapping) continue;
            if (group.Columns.Count is < 1 or > 256) errors.Add($"{label} 须配置 1–256 个字段映射。");
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var boundTag = false;
            foreach (var mapping in group.Columns)
            {
                if (mapping is null) { errors.Add($"{label} 包含空字段映射。"); continue; }
                if (!IsIdentifier(mapping.Column) || !columns.Add(mapping.Column)) errors.Add($"{label} 包含无效或重复的字段名 [{mapping.Column}]。");
                if (!Enum.IsDefined(mapping.Source) || !Enum.IsDefined(mapping.FieldType)) errors.Add($"{label} 字段 [{mapping.Column}] 的来源或转换类型无效。");
                if (mapping.Source is not (DatabaseValueSource.RecordTime or DatabaseValueSource.EventId or DatabaseValueSource.Constant))
                {
                    boundTag = true;
                    if (!mapping.TagId.HasValue || !tags.Contains(mapping.TagId.Value)) errors.Add($"{label} 字段 [{mapping.Column}] 须关联现有点位。");
                }
                if (mapping.ConstantValue is null || mapping.ConstantValue.Length > 4000) errors.Add($"{label} 字段 [{mapping.Column}] 常量须少于 4001 字符。");
                if (mapping.Source == DatabaseValueSource.Constant)
                {
                    try { _ = DatabaseRowMapper.ConvertField(mapping.ConstantValue, mapping.FieldType); }
                    catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException or ArgumentException)
                    { errors.Add($"{label} 字段 [{mapping.Column}] 的常量不符合所选类型：{exception.Message}"); }
                }
            }
            if (!boundTag) errors.Add($"{label} 的自定义映射须至少关联一个采集点位。");
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateConnection(DatabaseConnectionConfiguration connection)
    {
        var errors = new List<string>();
        var label = $"数据库 [{connection.Name}]";
        if (!Enum.IsDefined(connection.Provider)) errors.Add($"{label} 类型无效。");
        if (string.IsNullOrWhiteSpace(connection.Host) || connection.Host.Length > 253
            || (!System.Net.IPAddress.TryParse(connection.Host.Trim().Trim('[', ']'), out _) && !DatabaseHost().IsMatch(connection.Host.Trim())))
            errors.Add($"{label} 主机应为 IP 或域名，不接受连接字符串或协议前缀。");
        if (connection.Port is < 1 or > 65535) errors.Add($"{label} 端口须为 1–65535。");
        if (string.IsNullOrWhiteSpace(connection.Database) || connection.Database.Length > 128 || connection.Database.Any(char.IsControl))
            errors.Add($"{label} 数据库名/Oracle 服务名须为 1–128 字符。");
        if (connection.Provider == ExternalDatabaseProvider.Oracle && !OracleServiceName().IsMatch(connection.Database ?? ""))
            errors.Add($"{label} Oracle 服务名只接受字母、数字、点、下划线或短横线。");
        if (string.IsNullOrWhiteSpace(connection.Username) || connection.Username.Length > 128 || connection.Username.Any(char.IsControl)) errors.Add($"{label} 用户名不能为空、超过 128 字符或包含控制字符。");
        if (connection.Password is null) errors.Add($"{label} 密码不能为 null；无密码时请留空。");
        if (connection.ConnectionTimeoutSeconds is < 1 or > 120) errors.Add($"{label} 连接超时须为 1–120 秒。");
        if (connection.CommandTimeoutSeconds is < 1 or > 300) errors.Add($"{label} 写入超时须为 1–300 秒。");
        if (connection.BatchSize is < 1 or > 5000) errors.Add($"{label} 批次大小须为 1–5000 行。");
        if (connection.FlushIntervalMs is < 50 or > 60000) errors.Add($"{label} 刷新间隔须为 50–60000 ms。");
        if (connection.QueueCapacityRows is < 1 or > 1000000 || connection.QueueCapacityRows < connection.BatchSize)
            errors.Add($"{label} 队列行数须为 1–1000000 且不小于批次大小。");
        if (connection.QueueCapacityMb is < 1 or > 1024) errors.Add($"{label} 队列内存限额须为 1–1024 MiB。");
        if (connection.MaxRetryAttempts is < 1 or > 10) errors.Add($"{label} 每批最大尝试次数须为 1–10。");
        return errors;
    }

    public static bool IsIdentifier(string? value) => value is not null && System.Text.Encoding.UTF8.GetByteCount(value) <= 63 && Identifier().IsMatch(value);
    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 100 && !value.Any(char.IsControl);
    [GeneratedRegex("^[\\p{L}_][\\p{L}\\p{N}_]*$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)] private static partial Regex OracleServiceName();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.-]*$", RegexOptions.CultureInvariant)] private static partial Regex DatabaseHost();
}
