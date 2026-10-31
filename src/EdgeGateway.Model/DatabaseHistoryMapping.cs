namespace EdgeGateway.Model;

/// <summary>Describes the sample columns that can be read back without inventing identity, time or quality.</summary>
public static class DatabaseHistoryMapping
{
    private static readonly (DatabaseValueSource Source, string Column, string Name)[] Fields =
    [
        (DatabaseValueSource.EventId, "event_id", "事件标识"),
        (DatabaseValueSource.DeviceId, "device_id", "设备标识"),
        (DatabaseValueSource.DeviceName, "device_name", "设备名称"),
        (DatabaseValueSource.TagId, "tag_id", "点位标识"),
        (DatabaseValueSource.TagName, "tag_name", "点位名称"),
        (DatabaseValueSource.Address, "address", "地址"),
        (DatabaseValueSource.RawValue, "raw_value", "原始值"),
        (DatabaseValueSource.EngineeringValue, "value_text", "工程值"),
        (DatabaseValueSource.SampleTime, "timestamp", "采样时间"),
        (DatabaseValueSource.Quality, "quality", "质量"),
        (DatabaseValueSource.Unit, "unit", "单位"),
        (DatabaseValueSource.Error, "error", "错误信息")
    ];
    private static readonly DatabaseValueSource[] Required =
    [DatabaseValueSource.DeviceId, DatabaseValueSource.TagId, DatabaseValueSource.EngineeringValue,
        DatabaseValueSource.SampleTime, DatabaseValueSource.Quality];

    public static string? GetUnavailableReason(DatabaseWriteGroupConfiguration group)
    {
        if (group.Mode == DatabaseStorageMode.PointRows) return null;
        if (group.Mode != DatabaseStorageMode.PointMapping)
            return "整行字段映射包含多个点位，不能按单点历史查询；请使用采样日志或逐点写入组。";
        if (group.Columns.Any(column => string.IsNullOrWhiteSpace(column.Column))
            || group.Columns.Select(column => column.Column).Distinct(StringComparer.Ordinal).Count() != group.Columns.Count)
            return "字段映射包含空字段名或重复字段。";
        var missing = new List<string>();
        foreach (var (source, _, name) in Fields)
        {
            var matches = Matches(group, source).ToArray();
            if (matches.Length > 1) return $"值来源“{name}”映射到多个字段，无法确定历史查询字段。";
            if (matches.Length == 0)
            {
                if (Required.Contains(source)) missing.Add(name);
                continue;
            }
            if (source is DatabaseValueSource.DeviceId or DatabaseValueSource.TagId or DatabaseValueSource.Quality
                && matches[0].FieldType is not (DatabaseFieldType.Auto or DatabaseFieldType.Text))
                return $"值来源“{name}”需要文本类型，才能还原历史采样。";
            if (source == DatabaseValueSource.SampleTime
                && matches[0].FieldType is not (DatabaseFieldType.Auto or DatabaseFieldType.Timestamp))
                return "采样时间需要时间类型，才能按时间范围查询。";
        }
        return missing.Count == 0 ? null : "缺少历史查询映射：" + string.Join("、", missing) + "。请在写入组的字段映射中配置对应值来源。";
    }

    // The order matches the standard point-table projection; optional columns are SQL NULL.
    public static IReadOnlyList<string?> Columns(DatabaseWriteGroupConfiguration group)
    {
        if (GetUnavailableReason(group) is { } reason) throw new ArgumentException(reason, nameof(group));
        return Fields.Select(field => group.Mode == DatabaseStorageMode.PointRows
            ? (field.Source is DatabaseValueSource.Unit or DatabaseValueSource.Error ? null : field.Column)
            : Matches(group, field.Source).SingleOrDefault()?.Column).ToArray();
    }

    private static IEnumerable<DatabaseColumnMapping> Matches(DatabaseWriteGroupConfiguration group, DatabaseValueSource source)
    {
        var matches = group.Columns.Where(column => column.Source == source);
        // NumericValue is a valid fallback only when no full engineering-value column was stored.
        return source == DatabaseValueSource.EngineeringValue && !matches.Any()
            ? group.Columns.Where(column => column.Source == DatabaseValueSource.NumericValue) : matches;
    }
}
