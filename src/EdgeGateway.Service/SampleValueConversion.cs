using System.Globalization;
using System.Text.Json;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

internal static class SampleValueConversion
{
    public static object ConvertReadValue(TagConfiguration tag, object raw)
    {
        if (raw is double d && !double.IsFinite(d) || raw is float f && !float.IsFinite(f))
            throw new InvalidDataException("点位包含 NaN 或无穷大数值。");
        // A batch method's configured type selects each wire value, while its return value is an array/object.
        // Keep that shape intact instead of trying to coerce the batch into one floating-point scalar.
        if (raw is JsonElement { ValueKind: JsonValueKind.Array or JsonValueKind.Object } structured)
        {
            if (tag.Scale != 1 || tag.Offset != 0)
                throw new InvalidDataException("批量或结构化结果只支持倍率 1、偏移 0；请在单值缓存点位上配置工程量换算。");
            return structured.Clone();
        }
        if (TagDataTypes.IsStructured(tag.DataType)) return raw;
        if (TagDataTypes.IsString(tag.DataType)) return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
        if (tag.DataType == TagDataType.DateTime)
        {
            if (raw is DateTime or DateTimeOffset) return raw;
            if (DateTimeOffset.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at)) return at;
            throw new InvalidDataException("驱动返回值无法解析为日期时间。");
        }
        if (tag.DataType == TagDataType.Boolean)
        {
            if (raw is bool) return raw;
            if (bool.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out var boolean)) return boolean;
            throw new InvalidDataException("驱动返回值与布尔类型不符。");
        }
        if (tag.DataType == TagDataType.Bit && raw is bool bit) raw = bit ? 1 : 0;
        if (raw is bool) throw new InvalidDataException("驱动返回值与数值类型不符。");
        if (tag.DataType is TagDataType.Int64 or TagDataType.UInt64 or TagDataType.TimeStampMs or TagDataType.TimeStampS)
        {
            var number = decimal.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (decimal.Truncate(number) != number) throw new InvalidDataException("64 位整数点位返回了小数。");
            object integer = tag.DataType == TagDataType.UInt64 ? (object)checked((ulong)number) : checked((long)number);
            if (tag.Scale == 1 && tag.Offset == 0) return integer;
            return checked(number * (decimal)tag.Scale + (decimal)tag.Offset);
        }
        var numeric = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        var engineering = numeric * tag.Scale + tag.Offset;
        if (!double.IsFinite(numeric) || !double.IsFinite(engineering)) throw new InvalidDataException("点位包含 NaN 或无穷大数值。");
        // Identity conversion keeps native values intact; NumericValue is a separate approximate plotting projection.
        return tag.Scale == 1 && tag.Offset == 0 ? raw : engineering;
    }

    public static object ConvertWideIntegerWrite(TagConfiguration tag, string text)
    {
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var engineering) || tag.Scale == 0)
            throw new ArgumentException("请输入有效的 64 位整数工程值，且倍率不能为 0。");
        var raw = (engineering - checked((decimal)tag.Offset)) / checked((decimal)tag.Scale);
        if (decimal.Truncate(raw) != raw) throw new ArgumentException("工程值反算后不是整数，无法写入 64 位整数寄存器。");
        try { return tag.DataType == TagDataType.UInt64 ? (object)checked((ulong)raw) : checked((long)raw); }
        catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(text), "反算值超出 64 位整数范围。"); }
    }
}
