using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Ports;
using EdgeGateway.Model;

namespace EdgeGateway.Driver.Modbus;

internal sealed class ModbusRtuTransport(DeviceConfiguration configuration) : IModbusTransport
{
    private SerialPort? _port;
    private long _lastExchangeTimestamp;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var port = new SerialPort(configuration.SerialPortName, configuration.BaudRate,
            Enum.Parse<Parity>(configuration.Parity, true), configuration.DataBits,
            Enum.Parse<StopBits>(configuration.StopBits, true))
        {
            Handshake = Handshake.None,
            ReadTimeout = Math.Min(100, configuration.TimeoutMs),
            WriteTimeout = configuration.TimeoutMs,
            DtrEnable = false,
            RtsEnable = false
        };
        try
        {
            // SerialPort has no cancellable Open API. Keep synchronous OS calls off the UI
            // thread and check cancellation before and after; never abandon an opening task.
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                port.Open();
                cancellationToken.ThrowIfCancellationRequested();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();
            }, CancellationToken.None).ConfigureAwait(false);
            _port = port;
            _lastExchangeTimestamp = Stopwatch.GetTimestamp();
        }
        catch { port.Dispose(); throw; }
    }

    public async Task<byte[]> ExchangeAsync(byte unitId, byte[] pdu, CancellationToken cancellationToken)
    {
        var port = _port ?? throw new IOException("Modbus RTU 串口未打开，请重新连接。");
        var bitsPerCharacter = 1 + configuration.DataBits + (port.Parity == Parity.None ? 0 : 1) + (port.StopBits == StopBits.Two ? 2 : 1);
        var silentMs = configuration.BaudRate > 19200 ? 2 : Math.Max(2, (int)Math.Ceiling(3.5 * bitsPerCharacter * 1000 / configuration.BaudRate));
        var remainingSilence = silentMs - Stopwatch.GetElapsedTime(_lastExchangeTimestamp).TotalMilliseconds;
        if (remainingSilence > 0) await Task.Delay(TimeSpan.FromMilliseconds(remainingSilence), cancellationToken).ConfigureAwait(false);
        try
        {
            // SerialPort.BaseStream.ReadAsync cancellation is not reliable on every platform.
            // Bounded synchronous reads poll the linked deadline at most every 100 ms.
            return await Task.Run(() => Exchange(port, unitId, pdu, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _lastExchangeTimestamp = Stopwatch.GetTimestamp(); }
    }

    private byte[] Exchange(SerialPort port, byte unitId, byte[] pdu, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new byte[pdu.Length + 3];
        request[0] = unitId;
        pdu.CopyTo(request, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(request.Length - 2), ModbusCrc16.Compute(request.AsSpan(0, request.Length - 2)));
        port.DiscardInBuffer();
        port.Write(request, 0, request.Length);
        cancellationToken.ThrowIfCancellationRequested();
        var head = new byte[3];
        ReadExactly(port, head, 0, 3, cancellationToken);
        if (head[0] != unitId) throw new InvalidDataException("Modbus RTU 响应站号不匹配。");
        int length;
        if (head[1] == (pdu[0] | 0x80)) length = 5;
        else if (head[1] != pdu[0]) throw new InvalidDataException("Modbus RTU 响应功能码不匹配。");
        else if (pdu[0] is >= 1 and <= 4)
        {
            var quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
            var expected = pdu[0] is 1 or 2 ? (quantity + 7) / 8 : quantity * 2;
            if (head[2] != expected || head[2] > 250) throw new InvalidDataException("Modbus RTU 字节数与请求不一致。");
            length = head[2] + 5;
        }
        else if (pdu[0] is 5 or 6 or 16) length = 8;
        else throw new InvalidDataException("不支持的 Modbus RTU 响应。");
        var frame = new byte[length];
        head.CopyTo(frame, 0);
        ReadExactly(port, frame, 3, length - 3, cancellationToken);
        var expectedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(length - 2));
        if (ModbusCrc16.Compute(frame.AsSpan(0, length - 2)) != expectedCrc)
            throw new InvalidDataException("Modbus RTU CRC16 校验失败。");
        cancellationToken.ThrowIfCancellationRequested();
        return frame.AsSpan(1, length - 3).ToArray();
    }

    private static void ReadExactly(SerialPort port, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var end = offset + count;
        while (offset < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var received = port.Read(buffer, offset, end - offset);
                if (received == 0) throw new EndOfStreamException("Modbus RTU 串口未返回完整报文。");
                offset += received;
            }
            catch (TimeoutException) { cancellationToken.ThrowIfCancellationRequested(); }
        }
    }

    public ValueTask DisconnectAsync()
    {
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}

public static class ModbusCrc16
{
    /// <summary>Polynomial 0xA001, initial value 0xFFFF; transmit low byte first.</summary>
    public static ushort Compute(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }
}
