using System.Globalization;
using EdgeGateway.Model;

namespace EdgeGateway.DataAccess;

public sealed partial class ExternalDatabaseConnector
{
    internal static string FormatTimeZoneOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    internal static string PostgreSqlServerTimeZone()
    {
        var zone = TimeZoneInfo.Local;
        return !zone.HasIanaId && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
    }

    internal static string SessionTimeSql(ExternalDatabaseProvider provider, TimeZoneInfo? serverTimeZone = null,
        DateTimeOffset? now = null)
    {
        var offset = FormatTimeZoneOffset((serverTimeZone ?? TimeZoneInfo.Local).GetUtcOffset(now ?? DateTimeOffset.UtcNow));
        return provider switch
        {
            ExternalDatabaseProvider.MySql => $"SET SESSION time_zone = '{offset}', sql_mode = CONCAT_WS(',', @@SESSION.sql_mode, 'STRICT_ALL_TABLES')",
            ExternalDatabaseProvider.Oracle => $"ALTER SESSION SET TIME_ZONE = '{offset}'",
            _ => throw new ArgumentException("该数据库通过连接设置时区。", nameof(provider))
        };
    }

    internal static DateTimeOffset ReadPointTimestamp(DateTime value, ExternalDatabaseProvider provider,
        TimeZoneInfo? serverTimeZone = null)
    {
        var zone = serverTimeZone ?? TimeZoneInfo.Local;
        if (provider == ExternalDatabaseProvider.PostgreSql)
            return TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)), zone);
        // DATETIME / Oracle TIMESTAMP have no offset. Their fields now contain the server clock.
        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
