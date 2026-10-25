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
            var batches = ModbusReadPlanner.Build(tags);
            var results = new TagReadResult[batches.Sum(batch => batch.Points.Count)];
            foreach (var batch in batches)
                await ReadBatchAsync(batch, results, cancellationToken).ConfigureAwait(false);
            return results;
        }
        finally { _operation.Release(); }
    }

    private async Task ReadBatchAsync(ModbusReadBatch batch, TagReadResult[] results, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new byte[5];
        request[0] = batch.Function;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(1), batch.Address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), checked((ushort)batch.Quantity));
        try
        {
            var response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
            var isBit = batch.Function <= 2;
            var byteCount = isBit ? (batch.Quantity + 7) / 8 : batch.Quantity * 2;
            if (response.Length != byteCount + 2 || response[1] != byteCount)
                await FailFrameAsync("Modbus 响应字节数与请求不一致。").ConfigureAwait(false);
            foreach (var point in batch.Points)
            {
                var offset = point.Address - batch.Address;
                object raw = isBit ? (response[2 + offset / 8] & (1 << (offset % 8))) != 0
                    : RegisterCodec.Decode(response.AsSpan(2 + offset * 2, point.Quantity * 2), point.Tag.DataType, point.Tag.ByteOrder);
                results[point.Index] = raw is float f && !float.IsFinite(f) || raw is double d && !double.IsFinite(d)
                    ? new(point.Tag.Id, null, SampleQuality.Bad, "设备返回 NaN 或 Infinity。")
                    : new(point.Tag.Id, raw);
            }
        }
        catch (ModbusDeviceException ex) when (batch.Points.Count > 1 && ex.Code is 2 or 3)
        {
            // Some devices reject block boundaries/lengths, or one address is invalid.
            // At most one individual read per point; keep the caller's cycle deadline.
            // Never retry writes, malformed frames, timeouts, or busy/device-failure responses.
            foreach (var point in batch.Points)
                await ReadBatchAsync(new(point.Function, point.Address, point.Quantity, [point]), results, cancellationToken).ConfigureAwait(false);
        }
        catch (ModbusDeviceException ex)
        {
            foreach (var point in batch.Points)
                results[point.Index] = new(point.Tag.Id, null, SampleQuality.Bad, ex.Message);
        }
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
    public byte Code { get; } = code;
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
