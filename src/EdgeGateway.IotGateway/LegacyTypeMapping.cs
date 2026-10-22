using System.Globalization;
using System.Text.Json;
using EdgeGateway.Model;
using PluginInterface;

namespace EdgeGateway.IotGateway;

internal static class LegacyTypeMapping
{
    private static readonly JsonSerializerOptions NativeObjectOptions = new() { IncludeFields = true };
    public static DataTypeEnum ToLegacy(TagDataType type) => type switch
    {
        TagDataType.Boolean => DataTypeEnum.Bool, TagDataType.Bit => DataTypeEnum.Bit,
        TagDataType.UInt8 => DataTypeEnum.UByte, TagDataType.Int8 => DataTypeEnum.Byte,
        TagDataType.UInt16 => DataTypeEnum.Uint16, TagDataType.Int16 => DataTypeEnum.Int16,
        TagDataType.UInt32 => DataTypeEnum.Uint32, TagDataType.Int32 => DataTypeEnum.Int32,
        TagDataType.Float32 => DataTypeEnum.Float, TagDataType.Float64 => DataTypeEnum.Double,
        TagDataType.Bcd16 => DataTypeEnum.Bcd16, TagDataType.Bcd32 => DataTypeEnum.Bcd32,
        TagDataType.UInt64 => DataTypeEnum.Uint64, TagDataType.Int64 => DataTypeEnum.Int64,
        TagDataType.AsciiString => DataTypeEnum.AsciiString, TagDataType.Utf8String => DataTypeEnum.Utf8String,
        TagDataType.Gb2312String => DataTypeEnum.Gb2312String, TagDataType.DateTime => DataTypeEnum.DateTime,
        TagDataType.TimeStampMs => DataTypeEnum.TimeStampMs, TagDataType.TimeStampS => DataTypeEnum.TimeStampS,
        TagDataType.Any => DataTypeEnum.Any, TagDataType.Custom1 => DataTypeEnum.Custome1,
        TagDataType.Custom2 => DataTypeEnum.Custome2, TagDataType.Custom3 => DataTypeEnum.Custome3,
        TagDataType.Custom4 => DataTypeEnum.Custome4, TagDataType.Custom5 => DataTypeEnum.Custome5,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static EndianEnum ToLegacy(TagConfiguration tag) => tag.Endian switch
    {
        ProtocolEndian.None => EndianEnum.None,
        ProtocolEndian.BigEndian => EndianEnum.BigEndian,
        ProtocolEndian.LittleEndian => EndianEnum.LittleEndian,
        ProtocolEndian.BigEndianSwap => EndianEnum.BigEndianSwap,
        ProtocolEndian.LittleEndianSwap => EndianEnum.LittleEndianSwap,
        _ => tag.ByteOrder switch
        {
            RegisterByteOrder.ABCD => EndianEnum.BigEndian,
            RegisterByteOrder.BADC => EndianEnum.BigEndianSwap,
            RegisterByteOrder.CDAB => EndianEnum.LittleEndianSwap,
            RegisterByteOrder.DCBA => EndianEnum.LittleEndian,
            _ => throw new ArgumentOutOfRangeException(nameof(tag.ByteOrder))
        }
    };

    public static object? NormalizeValue(object? value)
    {
        if (value is null or DBNull) return null;
        if (value is Newtonsoft.Json.Linq.JToken token)
        {
            using var document = JsonDocument.Parse(token.ToString(Newtonsoft.Json.Formatting.None));
            return NormalizeElement(document.RootElement);
        }
        if (value is JsonElement element) return NormalizeElement(element);
        // Keep scalar CLR values, especially UInt64/Int64, rather than converting everything to double.
        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset)
            return value;
        // FOCAS returns native structures with public fields, not only CLR properties.
        return JsonSerializer.SerializeToElement(value, value.GetType(), NativeObjectOptions);
    }

    private static object? NormalizeElement(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) return element.GetBoolean();
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt64(out var signed)) return signed;
            if (element.TryGetUInt64(out var unsigned)) return unsigned;
            if (element.TryGetDecimal(out var precise)) return precise;
            return element.GetDouble();
        }
        return element.Clone();
    }

    public static object ParseParameter(string text, Type type)
    {
        if (type == typeof(string)) return text;
        if (type == typeof(bool))
        {
            if (bool.TryParse(text, out var boolean)) return boolean;
            return text switch { "1" => true, "0" => false, _ => throw new FormatException("布尔参数只接受 true/false/1/0。") };
        }
        if (type.IsEnum)
        {
            // Configuration stores enum names only. Numeric aliases are ambiguous to validation/UI and are rejected.
            var name = Enum.GetNames(type).FirstOrDefault(n => string.Equals(n, text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (name is null || !Enum.TryParse(type, name, ignoreCase: false, out var result) || result is null)
                throw new FormatException($"参数必须为 {string.Join(" / ", Enum.GetNames(type))}。");
            return result;
        }
        return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
    }
}
