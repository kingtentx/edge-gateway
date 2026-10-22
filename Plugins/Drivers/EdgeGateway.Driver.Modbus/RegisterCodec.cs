using System.Buffers.Binary;
using System.Globalization;
using EdgeGateway.Model;

namespace EdgeGateway.Driver.Modbus;

/// <summary>
/// Converts Modbus register bytes to raw values. ABCD = network order;
/// BADC swaps bytes within each 16-bit register; CDAB reverses the order of all
/// 16-bit registers; DCBA reverses all bytes. For Float64 ABCDEFGH these are
/// ABCDEFGH / BADCFEHG / GHEFCDAB / HGFEDCBA respectively.
/// For one register CDAB is identical to ABCD and DCBA is identical to BADC.
/// </summary>
public static class RegisterCodec
{
    public static int GetRegisterCount(TagDataType type) => type switch
    {
        TagDataType.Boolean or TagDataType.Int16 or TagDataType.UInt16 => 1,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 2,
        TagDataType.Float64 => 4,
        _ => throw new ArgumentException("不支持的数据类型。", nameof(type))
    };

    public static object Decode(ReadOnlySpan<byte> bytes, TagDataType type, RegisterByteOrder order)
    {
        if (bytes.Length != GetRegisterCount(type) * 2) throw new ArgumentException("寄存器字节长度与数据类型不一致。", nameof(bytes));
        Span<byte> canonical = stackalloc byte[8];
        bytes.CopyTo(canonical);
        Transform(canonical[..bytes.Length], order);
        return type switch
        {
            TagDataType.Boolean => BinaryPrimitives.ReadUInt16BigEndian(canonical) != 0,
            TagDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(canonical),
            TagDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(canonical),
            TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(canonical),
            TagDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(canonical),
            TagDataType.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(canonical)),
            TagDataType.Float64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(canonical)),
            _ => throw new ArgumentException("不支持的数据类型。", nameof(type))
        };
    }

    public static byte[] Encode(object value, TagDataType type, RegisterByteOrder order)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = new byte[GetRegisterCount(type) * 2];
        try
        {
            switch (type)
            {
                case TagDataType.Boolean: BinaryPrimitives.WriteUInt16BigEndian(bytes, ToBoolean(value) ? (ushort)1 : (ushort)0); break;
                case TagDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)ToInteger(value))); break;
                case TagDataType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)ToInteger(value))); break;
                case TagDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)ToInteger(value))); break;
                case TagDataType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)ToInteger(value))); break;
                case TagDataType.Float32:
                    var single = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                    if (!float.IsFinite(single)) throw new ArgumentException("浮点写入值必须是有限数值。");
                    BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(single));
                    break;
                case TagDataType.Float64:
                    var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(number)) throw new ArgumentException("浮点写入值必须是有限数值。");
                    BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(number));
                    break;
            }
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            throw new ArgumentException($"写入值无法转换为 {type}，请检查格式与范围。", nameof(value), ex);
        }
        Transform(bytes, order);
        return bytes;
    }

    public static bool ToBoolean(object value)
    {
        if (value is bool boolean) return boolean;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (bool.TryParse(text, out var flag)) return flag;
        if (text == "0") return false;
        if (text == "1") return true;
        throw new ArgumentException("Boolean 写入值必须为 true、false、0 或 1。", nameof(value));
    }

    private static decimal ToInteger(object value)
    {
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (number != decimal.Truncate(number)) throw new ArgumentException("整数点位不能写入小数。", nameof(value));
        return number;
    }

    private static void Transform(Span<byte> bytes, RegisterByteOrder order)
    {
        if (!Enum.IsDefined(order)) throw new ArgumentException("字节序无效。", nameof(order));
        if (order is RegisterByteOrder.BADC or RegisterByteOrder.DCBA)
            for (var i = 0; i < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        if (order is RegisterByteOrder.CDAB or RegisterByteOrder.DCBA)
            for (var left = 0; left < bytes.Length / 2; left += 2)
            {
                var right = bytes.Length - 2 - left;
                (bytes[left], bytes[right]) = (bytes[right], bytes[left]);
                (bytes[left + 1], bytes[right + 1]) = (bytes[right + 1], bytes[left + 1]);
            }
    }
}
