using System.Globalization;
using System.Text;
using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

internal static class DatabaseRowMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(GatewayJson.Options) { WriteIndented = false };

    public static DatabaseWriteRow PointRow(TelemetrySample sample)
    {
        var eventId = Guid.NewGuid().ToString("N");
        return new(eventId, DateTimeOffset.UtcNow, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_id"] = eventId,
            ["device_id"] = sample.DeviceId.ToString("D"), ["device_name"] = sample.DeviceName,
            ["tag_id"] = sample.TagId.ToString("D"), ["tag_name"] = sample.TagName, ["address"] = sample.Address,
            ["raw_value"] = JsonValue(sample.RawValue), ["value_text"] = JsonValue(sample.Value),
            ["timestamp"] = sample.Timestamp.ToUniversalTime(), ["quality"] = sample.Quality.ToString()
        });
    }

    public static DatabaseWriteRow MappedRow(DatabaseWriteGroupConfiguration group,
        IReadOnlyDictionary<Guid, TelemetrySample> samples, DateTimeOffset now)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var mapping in group.Columns)
        {
            var sample = mapping.TagId.HasValue ? samples.GetValueOrDefault(mapping.TagId.Value) : null;
            values.Add(mapping.Column, ResolveField(mapping, sample, eventId, now));
        }
        return new(eventId, now, values);
    }

    internal static object? ResolveField(DatabaseColumnMapping mapping, TelemetrySample? sample, string eventId, DateTimeOffset now)
    {
        if (mapping.Source is not (DatabaseValueSource.RecordTime or DatabaseValueSource.EventId or DatabaseValueSource.Constant) && sample is null)
            throw new InvalidDataException($"字段 [{mapping.Column}] 尚无关联点位采样。");
        object? value = mapping.Source switch
        {
            DatabaseValueSource.EngineeringValue => sample!.Value,
            DatabaseValueSource.RawValue => sample!.RawValue,
            DatabaseValueSource.NumericValue => sample!.NumericValue,
            DatabaseValueSource.SampleTime => sample!.Timestamp.ToUniversalTime(),
            DatabaseValueSource.Quality => sample!.Quality.ToString(),
            DatabaseValueSource.DeviceId => sample!.DeviceId.ToString("D"),
            DatabaseValueSource.DeviceName => sample!.DeviceName,
            DatabaseValueSource.TagId => sample!.TagId.ToString("D"),
            DatabaseValueSource.TagName => sample!.TagName,
            DatabaseValueSource.Address => sample!.Address,
            DatabaseValueSource.Unit => sample!.Unit,
            DatabaseValueSource.Error => sample!.Error,
            DatabaseValueSource.RecordTime => now,
            DatabaseValueSource.EventId => eventId,
            DatabaseValueSource.Constant => mapping.ConstantValue,
            _ => throw new InvalidDataException("不支持的字段值来源。")
        };
        try { return ConvertField(value, mapping.FieldType); }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException or ArgumentException)
        { throw new InvalidDataException($"字段 [{mapping.Column}] 不能转换为 {mapping.FieldType}：{exception.Message}", exception); }
    }

    public static object? ConvertField(object? value, DatabaseFieldType type)
    {
        value = Normalize(value);
        if (value is null) return null;
        return type switch
        {
            DatabaseFieldType.Auto => value,
            DatabaseFieldType.Text => value switch
            {
                DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            },
            DatabaseFieldType.Int64 => ExactInt64(value),
            DatabaseFieldType.Decimal => ExactDecimal(value),
            DatabaseFieldType.Double => FiniteDouble(value),
            DatabaseFieldType.Boolean => BooleanValue(value),
            DatabaseFieldType.Timestamp => TimestampValue(value),
            _ => throw new InvalidCastException("不支持的字段转换类型。")
        };
    }

    // A conservative serialized-size budget includes object/column overhead. This bounds
    // queued payload, not total CLR heap usage; individual rows above 1 MiB are rejected.
    public static long EstimateBytes(DatabaseWriteRow row)
    {
        long bytes = 512;
        foreach (var pair in row.Values)
            bytes += 96 + Encoding.UTF8.GetByteCount(pair.Key) + (pair.Value is string text ? Encoding.UTF8.GetByteCount(text) * 2L : 32);
        return bytes;
    }

    private static string? JsonValue(object? value) => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
    private static object? Normalize(object? value) => value switch
    {
        null => null,
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => FiniteDouble(element.GetDouble()),
            _ => element.GetRawText()
        },
        ulong integer => (decimal)integer,
        float number when !float.IsFinite(number) => throw new InvalidCastException("不接受 NaN 或无穷大。"),
        double number when !double.IsFinite(number) => throw new InvalidCastException("不接受 NaN 或无穷大。"),
        bool or string or char or byte or sbyte or short or ushort or int or uint or long or decimal or float or double or DateTime or DateTimeOffset => value,
        Guid id => id.ToString("D"),
        _ => JsonSerializer.Serialize(value, JsonOptions)
    };
    private static decimal ExactDecimal(object value)
    {
        if (value is decimal exact) return exact;
        var text = (value switch
        {
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
        }).Trim();
        if (text.Length > 128) throw new InvalidCastException("数值文本过长。");
        var result = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        // Decimal.Parse may silently round a >29-digit mantissa or turn 1e-29 into zero.
        if (CanonicalNumber(text) != CanonicalNumber(result.ToString("G29", CultureInfo.InvariantCulture)))
            throw new InvalidCastException("decimal 转换会损失精度或发生下溢，请选择文本保留原值或调整目标字段类型。");
        return result;
    }

    private static string CanonicalNumber(string text)
    {
        var negative = text.StartsWith('-');
        text = text.TrimStart('+', '-');
        var exponentAt = text.IndexOfAny(['e', 'E']);
        var exponent = exponentAt < 0 ? 0 : int.Parse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        var dot = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        exponent -= dot < 0 ? 0 : mantissa.Length - dot - 1;
        var significant = digits.TrimEnd('0');
        exponent += digits.Length - significant.Length;
        return (negative ? "-" : "") + significant + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }
    private static long ExactInt64(object value)
    {
        var number = ExactDecimal(value);
        if (decimal.Truncate(number) != number) throw new InvalidCastException("整数列不接受小数；请显式选择浮点或 decimal 列。");
        return checked((long)number);
    }
    private static double FiniteDouble(object value)
    {
        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new InvalidCastException("不接受 NaN 或无穷大。");
        return number;
    }
    private static bool BooleanValue(object value)
    {
        if (value is bool boolean) return boolean;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (bool.TryParse(text, out boolean)) return boolean;
        return text switch { "1" => true, "0" => false, _ => throw new InvalidCastException("布尔列只接受 true、false、1 或 0。") };
    }
    private static DateTimeOffset TimestampValue(object value) => value switch
    {
        DateTimeOffset timestamp => timestamp.ToUniversalTime(),
        DateTime timestamp => new DateTimeOffset(timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc) : timestamp).ToUniversalTime(),
        string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        _ => throw new InvalidCastException("时间列须为时间值或 ISO 8601 时间字符串。")
    };
}
