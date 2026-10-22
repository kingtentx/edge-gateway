using EdgeGateway.Driver.Modbus;
using EdgeGateway.Model;

namespace EdgeGateway.Tests.Drivers;

public sealed class RegisterCodecTests
{
    [Theory]
    [InlineData(RegisterByteOrder.ABCD, "01020304")]
    [InlineData(RegisterByteOrder.BADC, "02010403")]
    [InlineData(RegisterByteOrder.CDAB, "03040102")]
    [InlineData(RegisterByteOrder.DCBA, "04030201")]
    public void UInt32_matches_independent_wire_vectors(RegisterByteOrder order, string hex)
    {
        var wire = Convert.FromHexString(hex);
        Assert.Equal(wire, RegisterCodec.Encode(0x01020304u, TagDataType.UInt32, order));
        Assert.Equal(0x01020304u, Assert.IsType<uint>(RegisterCodec.Decode(wire, TagDataType.UInt32, order)));
    }

    [Theory]
    [InlineData(RegisterByteOrder.ABCD, "0102030405060708")]
    [InlineData(RegisterByteOrder.BADC, "0201040306050807")]
    [InlineData(RegisterByteOrder.CDAB, "0708050603040102")]
    [InlineData(RegisterByteOrder.DCBA, "0807060504030201")]
    public void Float64_word_order_covers_all_four_registers(RegisterByteOrder order, string hex)
    {
        var number = BitConverter.Int64BitsToDouble(0x0102030405060708);
        Assert.Equal(Convert.FromHexString(hex), RegisterCodec.Encode(number, TagDataType.Float64, order));
        Assert.Equal(number, Assert.IsType<double>(RegisterCodec.Decode(Convert.FromHexString(hex), TagDataType.Float64, order)));
    }

    [Fact]
    public void Values_preserve_signed_types_and_ieee754()
    {
        Assert.Equal((short)-2, Assert.IsType<short>(RegisterCodec.Decode(Convert.FromHexString("FFFE"), TagDataType.Int16, RegisterByteOrder.ABCD)));
        Assert.Equal((ushort)65535, Assert.IsType<ushort>(RegisterCodec.Decode(Convert.FromHexString("FFFF"), TagDataType.UInt16, RegisterByteOrder.ABCD)));
        Assert.Equal(int.MinValue, Assert.IsType<int>(RegisterCodec.Decode(Convert.FromHexString("80000000"), TagDataType.Int32, RegisterByteOrder.ABCD)));
        Assert.Equal(1.0f, Assert.IsType<float>(RegisterCodec.Decode(Convert.FromHexString("3F800000"), TagDataType.Float32, RegisterByteOrder.ABCD)));
        Assert.True(Assert.IsType<bool>(RegisterCodec.Decode(Convert.FromHexString("0001"), TagDataType.Boolean, RegisterByteOrder.ABCD)));
        Assert.Equal(Convert.FromHexString("FFFF"), RegisterCodec.Encode(-1, TagDataType.Int16, RegisterByteOrder.ABCD));
    }

    [Fact]
    public void Invalid_integer_and_nonfinite_writes_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => RegisterCodec.Encode(1.5, TagDataType.UInt16, RegisterByteOrder.ABCD));
        Assert.Throws<ArgumentException>(() => RegisterCodec.Encode(-1, TagDataType.UInt16, RegisterByteOrder.ABCD));
        Assert.Throws<ArgumentException>(() => RegisterCodec.Encode(65536, TagDataType.UInt16, RegisterByteOrder.ABCD));
        Assert.Throws<ArgumentException>(() => RegisterCodec.Encode(double.NaN, TagDataType.Float64, RegisterByteOrder.ABCD));
        Assert.Throws<ArgumentException>(() => RegisterCodec.Encode(double.PositiveInfinity, TagDataType.Float32, RegisterByteOrder.ABCD));
        Assert.Throws<ArgumentException>(() => RegisterCodec.Encode(2, TagDataType.Boolean, RegisterByteOrder.ABCD));
        Assert.Throws<ArgumentException>(() => RegisterCodec.Decode([0, 1], TagDataType.Float32, RegisterByteOrder.ABCD));
    }

    [Fact]
    public void Crc_matches_standard_request_vector()
    {
        // 01 03 00 00 00 0A C5 CD: wire CRC is little endian C5 CD.
        Assert.Equal((ushort)0xCDC5, ModbusCrc16.Compute(Convert.FromHexString("01030000000A")));
    }

    [Fact]
    public void Validation_rejects_cross_boundary_spans_readonly_writes_and_nonboolean_coils()
    {
        var factory = new ModbusTcpDriverFactory();
        var configuration = new DeviceConfiguration
        {
            Tags =
            [
                new() { Name = "跨界", Address = "65535", DataType = TagDataType.Float32 },
                new() { Name = "只读区", Address = "0", Area = ModbusArea.InputRegister, Writable = true },
                new() { Name = "线圈类型", Address = "0", Area = ModbusArea.Coil, DataType = TagDataType.Int16 },
                new() { Name = "负地址", Address = "-1" }
            ]
        };
        Assert.Equal(4, factory.Validate(configuration).Count);
        Assert.Throws<ArgumentException>(() => factory.Create(configuration));
    }
}
