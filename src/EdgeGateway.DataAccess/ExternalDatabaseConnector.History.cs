using System.Data.Common;
using System.Globalization;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

public sealed partial class ExternalDatabaseConnector
{
    private Task<IReadOnlyList<TelemetrySample>> QueryMappedPointHistoryAsync(DatabaseConnectionConfiguration connection,
        DatabaseWriteGroupConfiguration group, HistoryQuery query, CancellationToken cancellationToken)
    {
        if (query.From > query.To) throw new ArgumentException("查询开始时间不能晚于结束时间。");
        var columns = DatabaseHistoryMapping.Columns(group);
        var provider = connection.Provider;
        var table = QualifiedTable(provider, Table(group));
        // Quote every configured identifier before opening a connection; filter values remain parameters.
        var projection = string.Join(", ", columns.Select(column => column is null ? "NULL" : Q(provider, column)));
        return WithConnectionAsync<IReadOnlyList<TelemetrySample>>(connection, async (database, token) =>
        {
            var metadata = await ReadColumnsAsync(database, connection, Table(group), token).ConfigureAwait(false);
            foreach (var column in columns.OfType<string>())
                if (!metadata.Any(item => item.Name == column))
                    throw new ConnectorConfigurationException($"历史映射字段 [{column}] 不在目标表中，请刷新并核对字段映射。");
            foreach (var ordinal in new[] { 1, 3, 9 })
            {
                var type = metadata.Single(item => item.Name == columns[ordinal]).DataType;
                if (!IsHistoryTextType(type))
                    throw new ConnectorConfigurationException($"历史映射字段 [{columns[ordinal]}] 必须为文本列，当前类型为 {type}。");
            }
            var timestampType = metadata.Single(item => item.Name == columns[8]).DataType;
            if (!SupportsHistoryTimestamp(provider, timestampType))
                throw new ConnectorConfigurationException($"采样时间字段 [{columns[8]}] 的类型 {timestampType} 暂不支持历史范围查询。请使用采样日志或受支持的时间列。");
            var sql = $"SELECT {projection} FROM {table} WHERE {Q(provider, columns[8]!)} >= {Parameter(provider, "from_time")}"
                + $" AND {Q(provider, columns[8]!)} <= {Parameter(provider, "to_time")}";
            if (query.DeviceId.HasValue) sql += $" AND {Q(provider, columns[1]!)} = {Parameter(provider, "device")}";
            if (query.TagId.HasValue) sql += $" AND {Q(provider, columns[3]!)} = {Parameter(provider, "tag")}";
            sql += $" ORDER BY {Q(provider, columns[8]!)} DESC, {Q(provider, columns[0] ?? columns[3]!)} DESC";
            sql += provider == ExternalDatabaseProvider.Oracle ? " FETCH FIRST :row_limit ROWS ONLY" : " LIMIT @row_limit";
            await using var command = Command(database, connection, sql);
            AddValueParameter(command, provider, "from_time", query.From, DatabaseFieldType.Timestamp, timestampType);
            AddValueParameter(command, provider, "to_time", query.To, DatabaseFieldType.Timestamp, timestampType);
            if (query.DeviceId.HasValue) AddValueParameter(command, provider, "device", query.DeviceId.Value.ToString("D"));
            if (query.TagId.HasValue) AddValueParameter(command, provider, "tag", query.TagId.Value.ToString("D"));
            AddValueParameter(command, provider, "row_limit", Math.Clamp(query.Limit, 1, 10000));
            var result = new List<TelemetrySample>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                result.Add(ReadMappedHistorySample(reader, provider, timestampType));
            return result;
        }, cancellationToken);
    }

    private static bool IsHistoryTextType(string type) => type.Contains("char", StringComparison.OrdinalIgnoreCase)
        || type.Equals("text", StringComparison.OrdinalIgnoreCase) || type.EndsWith("text", StringComparison.OrdinalIgnoreCase);

    internal static bool SupportsHistoryTimestamp(ExternalDatabaseProvider provider, string type)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(type, @"\(\d+\)", "").Trim().ToLowerInvariant();
        return provider switch
        {
            ExternalDatabaseProvider.PostgreSql => normalized is "timestamp with time zone" or "timestamp without time zone" or "timestamp",
            ExternalDatabaseProvider.MySql => normalized == "datetime",
            ExternalDatabaseProvider.Oracle => normalized is "timestamp" or "date",
            _ => false
        };
    }

    internal static TelemetrySample ReadMappedHistorySample(DbDataReader reader, ExternalDatabaseProvider provider, string timestampType)
    {
        string ReadText(int ordinal) => reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
        if (!Guid.TryParse(ReadText(1), out var deviceId) || deviceId == Guid.Empty
            || !Guid.TryParse(ReadText(3), out var tagId) || tagId == Guid.Empty
            || !Enum.TryParse<SampleQuality>(ReadText(9), out var quality) || !Enum.IsDefined(quality))
            throw new ConnectorConfigurationException("历史记录含无效设备标识、点位标识或质量码，请核对原写入映射；不能用当前配置修复已写入的记录。");
        if (reader.IsDBNull(8) || reader.GetValue(8) is not DateTime timestamp)
            throw new ConnectorConfigurationException("历史记录的采样时间为空或不是有效时间。");
        var zoned = provider == ExternalDatabaseProvider.PostgreSql
            && timestampType.Contains("with time zone", StringComparison.OrdinalIgnoreCase);
        // Unzoned columns contain the acquisition server's wall clock, including PostgreSQL timestamp.
        var instant = ReadPointTimestamp(timestamp, zoned ? ExternalDatabaseProvider.PostgreSql : ExternalDatabaseProvider.MySql);
        var error = ReadText(11);
        return new(deviceId, ReadText(2), tagId, ReadText(4), ReadText(5),
            reader.IsDBNull(6) ? null : NormalizeValue(reader.GetValue(6)),
            reader.IsDBNull(7) ? null : NormalizeValue(reader.GetValue(7)), quality, instant, ReadText(10),
            error.Length == 0 ? null : error);
    }
}
