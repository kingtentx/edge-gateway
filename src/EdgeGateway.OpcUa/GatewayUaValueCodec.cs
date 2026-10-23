using System.Globalization;
using System.Text.Json;
using EdgeGateway.Model;
using Opc.Ua;

namespace EdgeGateway.OpcUa;

internal static class GatewayUaValueCodec
{
    private static bool IsTransformed(TagConfiguration tag) => TagDataTypes.IsNumeric(tag.DataType)
        && (tag.Scale != 1 || tag.Offset != 0 || !string.IsNullOrWhiteSpace(tag.Expression));
    private static bool IsJson(TagConfiguration tag) => TagDataTypes.IsStructured(tag.DataType) || tag.ReadMethod == "ReadMultiple";
    public static NodeId DataType(TagConfiguration tag)
    {
        if (IsJson(tag) || TagDataTypes.IsString(tag.DataType)) return DataTypeIds.String;
        if (IsTransformed(tag)) return DataTypeIds.Double;
        return tag.DataType switch
        {
            TagDataType.Boolean => DataTypeIds.Boolean, TagDataType.Int8 => DataTypeIds.SByte, TagDataType.UInt8 => DataTypeIds.Byte,
            TagDataType.Int16 => DataTypeIds.Int16, TagDataType.UInt16 or TagDataType.Bcd16 => DataTypeIds.UInt16,
            TagDataType.Bit or TagDataType.Int32 => DataTypeIds.Int32, TagDataType.UInt32 or TagDataType.Bcd32 => DataTypeIds.UInt32,
            TagDataType.Int64 or TagDataType.TimeStampMs or TagDataType.TimeStampS => DataTypeIds.Int64,
            TagDataType.UInt64 => DataTypeIds.UInt64, TagDataType.Float32 => DataTypeIds.Float, TagDataType.Float64 => DataTypeIds.Double,
            TagDataType.DateTime => DataTypeIds.DateTime, _ => DataTypeIds.String
        };
    }
    public static bool SupportsWrite(TagConfiguration tag) => tag.Writable && string.IsNullOrWhiteSpace(tag.Expression)
        && !IsJson(tag) && tag.DataType != TagDataType.DateTime;

    public static object ConvertValue(TagConfiguration tag, object value)
    {
        if (IsJson(tag)) return JsonSerializer.Serialize(value);
        if (TagDataTypes.IsString(tag.DataType)) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (tag.DataType == TagDataType.Boolean) return value is bool boolean ? boolean : throw new InvalidDataException("布尔点位未返回 Boolean。");
        if (tag.DataType == TagDataType.DateTime) return value switch
        {
            DateTime time => time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime(),
            DateTimeOffset time => time.UtcDateTime,
            _ => DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
        };
        if (IsTransformed(tag) || tag.DataType is TagDataType.Float32 or TagDataType.Float64)
        {
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number)) throw new InvalidDataException("OPC UA 数值不能为 NaN 或 Infinity。");
            if (tag.DataType == TagDataType.Float32 && !IsTransformed(tag))
            {
                if (Math.Abs(number) > float.MaxValue) throw new OverflowException("Float 数值超界。");
                return (float)number;
            }
            return number;
        }
        // Decimal and checked casts retain every bit of unscaled Int64/UInt64 and refuse fractional integer data.
        var integer = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (integer != decimal.Truncate(integer)) throw new InvalidDataException("整数点位返回了小数。");
        return tag.DataType switch
        {
            TagDataType.Int8 => (object)checked((sbyte)integer), TagDataType.UInt8 => (object)checked((byte)integer),
            TagDataType.Int16 => (object)checked((short)integer), TagDataType.UInt16 or TagDataType.Bcd16 => (object)checked((ushort)integer),
            TagDataType.Bit or TagDataType.Int32 => (object)checked((int)integer), TagDataType.UInt32 or TagDataType.Bcd32 => (object)checked((uint)integer),
            TagDataType.UInt64 => (object)checked((ulong)integer),
            TagDataType.Int64 or TagDataType.TimeStampMs or TagDataType.TimeStampS => (object)checked((long)integer),
            _ => throw new InvalidDataException("该值不能映射到声明的 OPC UA 标量类型。")
        };
    }

    public static string EngineeringText(object value) => value switch
    {
        string text => text, bool boolean => boolean ? "true" : "false",
        float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw new InvalidDataException("只允许写入与点位匹配的有限数值、Boolean 或字符串标量。")
    };
}
