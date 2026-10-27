using EdgeGateway.Application;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal static class DatabaseUiText
{
    internal static DatabaseWriteGroupConfiguration NewWriteGroup(Guid connectionId, IReadOnlyList<DatabaseWriteGroupConfiguration> groups)
    {
        var draft = new DatabaseWriteGroupConfiguration { ConnectionId = connectionId };
        var names = groups.Select(group => group.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = draft.Name;
        for (var suffix = 2; names.Contains(draft.Name); suffix++) draft.Name = $"{baseName}（{suffix}）";
        return draft;
    }

    internal static string Provider(ExternalDatabaseProvider value) => value switch
    { ExternalDatabaseProvider.MySql => "MySQL", ExternalDatabaseProvider.PostgreSql => "PostgreSQL", _ => "Oracle" };
    internal static string Mode(DatabaseStorageMode value) => value switch
    {
        DatabaseStorageMode.PointRows => "标准点行（每点一行）",
        DatabaseStorageMode.PointMapping => "自定义逐点（每点一行）",
        _ => "自定义整行（固定点位组合）"
    };
    internal static string Source(DatabaseValueSource value) => value switch
    {
        DatabaseValueSource.EngineeringValue => "value_text [工程值]", DatabaseValueSource.RawValue => "raw_value [原始值]",
        DatabaseValueSource.NumericValue => "NumericValue [数值·派生属性]",
        DatabaseValueSource.SampleTime => "timestamp [采样时间]", DatabaseValueSource.Quality => "quality [质量]",
        DatabaseValueSource.DeviceId => "device_id [设备标识]", DatabaseValueSource.DeviceName => "device_name [设备名称]",
        DatabaseValueSource.TagId => "tag_id [点位标识]", DatabaseValueSource.TagName => "tag_name [点位名称]",
        DatabaseValueSource.Address => "address [点位地址]", DatabaseValueSource.Unit => "Unit [单位·采样属性]",
        DatabaseValueSource.Error => "Error [错误说明·采样属性]", DatabaseValueSource.RecordTime => "RecordTime [记录时间·生成项]",
        DatabaseValueSource.EventId => "event_id [记录编号]", DatabaseValueSource.Constant => "Constant [手动常量]",
        _ => "不写入 [默认值/NULL]"
    };
    internal static string FieldType(DatabaseFieldType value) => value switch
    { DatabaseFieldType.Auto => "自动", DatabaseFieldType.Text => "文本", DatabaseFieldType.Int64 => "整数", DatabaseFieldType.Decimal => "精确小数", DatabaseFieldType.Double => "浮点", DatabaseFieldType.Boolean => "布尔", _ => "时间" };
    internal static bool NeedsPoint(DatabaseValueSource source) => source is not (DatabaseValueSource.RecordTime or DatabaseValueSource.EventId or DatabaseValueSource.Constant);
    internal sealed record EnumItem<T>(T Value, string Text);
    internal sealed record PointItem(Guid Id, string Text) { public override string ToString() => Text; }
}
