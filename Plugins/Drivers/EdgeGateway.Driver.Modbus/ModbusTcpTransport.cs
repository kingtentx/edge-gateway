using System.Buffers.Binary;
using System.Net.Sockets;
using EdgeGateway.Model;

namespace EdgeGateway.Driver.Modbus;

internal sealed class ModbusTcpTransport(DeviceConfiguration configuration) : IModbusTransport
{
    private TcpClient? _client;
    private ushort _transaction;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync().ConfigureAwait(false);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(configuration.Host, configuration.Port, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _client = client;
        }
        catch { client.Dispose(); throw; }
    }

    public async Task<byte[]> ExchangeAsync(byte unitId, byte[] pdu, CancellationToken cancellationToken)
    {
        if (_client is null) throw new IOException("Modbus TCP 尚未连接或已断开，请重新连接。");
        if (pdu.Length is < 1 or > 253) throw new ArgumentException("Modbus PDU 长度无效。", nameof(pdu));
        var transaction = unchecked(++_transaction);
        var request = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request, transaction);
        // Protocol identifier bytes 2/3 remain zero.
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), checked((ushort)(pdu.Length + 1)));
        request[6] = unitId;
        pdu.CopyTo(request, 7);
        var stream = _client.GetStream();
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != transaction)
            throw new InvalidDataException("Modbus TCP 事务号不匹配，可能收到过期响应。");
        if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0)
            throw new InvalidDataException("Modbus TCP 协议标识必须为 0。");
        if (header[6] != unitId) throw new InvalidDataException("Modbus TCP 响应站号不匹配。");
        if (length is < 2 or > 254) throw new InvalidDataException("Modbus TCP MBAP 长度越界。");
        var response = new byte[length - 1];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public ValueTask DisconnectAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}
