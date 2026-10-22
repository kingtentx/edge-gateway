using System.Buffers.Binary;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Driver.Modbus;

internal sealed class ModbusDriver(DeviceConfiguration configuration, IModbusTransport transport) : IDeviceDriver
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private bool _disposed;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(configuration.TimeoutMs);
            try { await transport.ConnectAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await transport.DisconnectAsync().ConfigureAwait(false);
                throw new TimeoutException($"Modbus 连接超时（{configuration.TimeoutMs} ms）。");
            }
        }
        finally { _operation.Release(); }
    }

    public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var results = new List<TagReadResult>(tags.Count);
            foreach (var tag in tags.Where(t => t.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var address = ModbusValidation.ValidateTag(tag);
                var isBit = tag.Area is ModbusArea.Coil or ModbusArea.DiscreteInput;
                var quantity = isBit ? 1 : RegisterCodec.GetRegisterCount(tag.DataType);
                var function = tag.Area switch
                {
                    ModbusArea.Coil => (byte)1,
                    ModbusArea.DiscreteInput => (byte)2,
                    ModbusArea.HoldingRegister => (byte)3,
                    ModbusArea.InputRegister => (byte)4,
                    _ => throw new ArgumentException("寄存区无效。")
                };
                var request = new byte[5];
                request[0] = function;
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(1), address);
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), checked((ushort)quantity));
                try
                {
                    var response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
                    var byteCount = isBit ? 1 : quantity * 2;
                    if (response.Length != byteCount + 2 || response[1] != byteCount)
                        await FailFrameAsync("Modbus 响应字节数与请求不一致。").ConfigureAwait(false);
                    object raw = isBit ? (response[2] & 1) != 0 : RegisterCodec.Decode(response.AsSpan(2), tag.DataType, tag.ByteOrder);
                    if (raw is float f && !float.IsFinite(f) || raw is double d && !double.IsFinite(d))
                        results.Add(new(tag.Id, null, SampleQuality.Bad, "设备返回 NaN 或 Infinity。"));
                    else
                        results.Add(new(tag.Id, raw));
                }
                catch (ModbusDeviceException ex) { results.Add(new(tag.Id, null, SampleQuality.Bad, ex.Message)); }
            }
            return results;
        }
        finally { _operation.Release(); }
    }

    public async Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken)
    {
        if (!tag.Enabled || !tag.Writable) throw new InvalidOperationException("该点位未启用或未允许写入。");
        var address = ModbusValidation.ValidateTag(tag);
        if (tag.Area is ModbusArea.DiscreteInput or ModbusArea.InputRegister)
            throw new InvalidOperationException("离散输入和输入寄存器不允许写入。");
        var bytes = tag.Area == ModbusArea.Coil ? Array.Empty<byte>() : RegisterCodec.Encode(value, tag.DataType, tag.ByteOrder);
        var request = new byte[bytes.Length > 2 ? 6 + bytes.Length : 5];
        request[0] = tag.Area == ModbusArea.Coil ? (byte)5 : bytes.Length == 2 ? (byte)6 : (byte)16;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(1), address);
        if (tag.Area == ModbusArea.Coil)
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), RegisterCodec.ToBoolean(value) ? (ushort)0xFF00 : (ushort)0);
        else if (bytes.Length == 2)
            bytes.CopyTo(request, 3);
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), checked((ushort)(bytes.Length / 2)));
            request[5] = checked((byte)bytes.Length);
            bytes.CopyTo(request, 6);
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Length != 5 || !response.AsSpan().SequenceEqual(request.AsSpan(0, 5)))
                await FailFrameAsync("Modbus 写入回显不匹配，写入结果不确定；不会自动重试写入。").ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }

    private async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(configuration.TimeoutMs);
        try
        {
            var response = await transport.ExchangeAsync(checked((byte)configuration.UnitId), request, deadline.Token).ConfigureAwait(false);
            if (response.Length == 0) throw new InvalidDataException("Modbus 响应缺少功能码。");
            if (response[0] == (request[0] | 0x80))
            {
                if (response.Length != 2) throw new InvalidDataException("Modbus 异常响应长度错误。");
                throw new ModbusDeviceException(response[1]);
            }
            if (response[0] != request[0]) throw new InvalidDataException("Modbus 响应功能码不匹配。");
            return response;
        }
        catch (ModbusDeviceException) { throw; }
        catch (Exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await transport.DisconnectAsync().ConfigureAwait(false);
            throw new TimeoutException($"Modbus 请求超时（{configuration.TimeoutMs} ms）；连接已关闭，写请求不会自动重试。");
        }
        catch
        {
            // A cancelled, truncated or malformed exchange has an uncertain framing boundary.
            // Reconnect before any later request, rather than consuming a stale response.
            await transport.DisconnectAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task FailFrameAsync(string message)
    {
        await transport.DisconnectAsync().ConfigureAwait(false);
        throw new InvalidDataException(message);
    }

    public async ValueTask DisposeAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await transport.DisconnectAsync().ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }
}

internal sealed class ModbusDeviceException(byte code) : IOException($"Modbus 设备异常 0x{code:X2}：{Describe(code)}")
{
    private static string Describe(byte code) => code switch
    {
        1 => "不支持的功能码", 2 => "非法数据地址", 3 => "非法数据值", 4 => "设备故障",
        5 => "设备已确认，尚未完成", 6 => "设备忙", 10 => "网关路径不可用", 11 => "目标设备未响应", _ => "未知设备异常"
    };
}

internal interface IModbusTransport
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task<byte[]> ExchangeAsync(byte unitId, byte[] pdu, CancellationToken cancellationToken);
    ValueTask DisconnectAsync();
}
