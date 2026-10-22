using Microsoft.Extensions.Logging;
using PluginInterface;
using SimpleTCP;
using System.Buffers.Binary;

namespace Mock.TcpClient
{
    // Migrated from iioter/iotgateway. The original 01 02 03 04 request and
    // eight-byte response beginning with 08 are preserved.
    [DriverSupported("SimTcpClient")]
    [DriverInfo("SimTcpClient", "V1.0.0", "Copyright iotgateway.net 20230220")]
    public class DeviceTcpClient : IDriver
    {
        private SimpleTcpClient? _client;
        private byte[]? _latestRcvData;
        private readonly object _receiveGate = new();
        private readonly List<byte> _pendingBytes = new(8);
        private readonly ManualResetEventSlim _responseReady = new(false);
        private bool _awaitingResponse;
        private readonly byte[] _sendCmd = { 0x01, 0x02, 0x03, 0x04 };
        public ILogger _logger { get; set; }
        private readonly string _device;

        [ConfigParameter("设备Id")] public string DeviceId { get; set; }
        [ConfigParameter("IP地址")] public string IpAddress { get; set; } = "127.0.0.1";
        [ConfigParameter("端口号")] public int Port { get; set; } = 6666;
        // Retained source configuration. The example protocol uses a persistent connection.
        [ConfigParameter("连接类型")]
        public ConnectionType ConnectionType { get; set; } = ConnectionType.Long;
        [ConfigParameter("超时时间ms")] public int Timeout { get; set; } = 300;
        [ConfigParameter("最小通讯周期ms")] public uint MinPeriod { get; set; } = 3000;

        public DeviceTcpClient(string device, ILogger logger)
        {
            _device = device;
            _logger = logger;
        }

        public bool IsConnected => _client?.TcpClient?.Connected == true;

        public bool Connect()
        {
            try
            {
                Close();
                if (Timeout <= 0) throw new ArgumentOutOfRangeException(nameof(Timeout));
                _client = new SimpleTcpClient();
                _client.DataReceived += Client_DataReceived;
                _client.Connect(IpAddress, Port);
                _client.TcpClient.SendTimeout = Timeout;
                return IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device:[{Device}], TCP 示例协议连接失败", _device);
                Close();
                return false;
            }
        }

        private void Client_DataReceived(object? sender, Message e)
        {
            lock (_receiveGate)
            {
                if (!_awaitingResponse) return;
                foreach (byte value in e.Data)
                {
                    if (_pendingBytes.Count == 0 && value != 0x08) continue;
                    _pendingBytes.Add(value);
                    if (_pendingBytes.Count != 8) continue;
                    _latestRcvData = _pendingBytes.ToArray();
                    _pendingBytes.Clear();
                    _awaitingResponse = false;
                    _responseReady.Set();
                    break;
                }
            }
        }

        public bool Close()
        {
            var client = _client;
            _client = null;
            lock (_receiveGate)
            {
                _awaitingResponse = false;
                _latestRcvData = null;
                _pendingBytes.Clear();
                _responseReady.Set();
            }
            try
            {
                if (client != null)
                {
                    client.DataReceived -= Client_DataReceived;
                    try { client.Disconnect(); }
                    finally { client.Dispose(); }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device:[{Device}], TCP 连接关闭失败", _device);
                return false;
            }
        }

        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }

        [Method("读模拟设备数据", description: "读模拟设备数据,开始字节和长度")]
        public DriverReturnValueModel Read(DriverAddressIoArgModel ioArg)
        {
            var ret = new DriverReturnValueModel { StatusType = VaribaleStatusTypeEnum.Good };
            try
            {
                if (!int.TryParse(ioArg.Address, out int startIndex) || startIndex < 0)
                    throw new FormatException("起始字节编号错误。");
                int width = ioArg.ValueType switch
                {
                    DataTypeEnum.UByte or DataTypeEnum.Byte => 1,
                    DataTypeEnum.Int16 => 2,
                    DataTypeEnum.Float => 4,
                    _ => throw new NotSupportedException("该示例协议仅支持 UByte、Byte、Int16 和 Float。"),
                };
                if (startIndex > 8 - width) throw new FormatException("点位超出 8 字节响应范围。");
                if (!IsConnected) throw new IOException("连接失败。");
                lock (_receiveGate)
                {
                    _latestRcvData = null;
                    _pendingBytes.Clear();
                    _responseReady.Reset();
                    _awaitingResponse = true;
                }
                _client!.Write(_sendCmd);
                if (!_responseReady.Wait(Timeout))
                {
                    Close(); // No request ID in this protocol: discard uncertain late replies.
                    throw new TimeoutException("超时未收到完整的新响应。");
                }
                byte[] frame;
                lock (_receiveGate)
                    frame = _latestRcvData?.ToArray() ?? throw new IOException("连接已关闭，未收到新的完整响应。");
                var bytes = frame.AsSpan(startIndex, width);
                ret.Value = ioArg.ValueType switch
                {
                    DataTypeEnum.UByte => (object)bytes[0],
                    DataTypeEnum.Byte => unchecked((sbyte)bytes[0]),
                    DataTypeEnum.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
                    DataTypeEnum.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes)),
                    _ => throw new NotSupportedException(),
                };
            }
            catch (Exception ex)
            {
                lock (_receiveGate) _awaitingResponse = false;
                ret.StatusType = VaribaleStatusTypeEnum.Bad;
                ret.Message = $"读取失败，{ex.Message}";
            }
            return ret;
        }

        public Task<RpcResponse> WriteAsync(string requestId, string method, DriverAddressIoArgModel ioArg)
            => Task.FromResult(new RpcResponse { IsSuccess = false, Description = "设备驱动内未实现写入功能" });
    }

    public enum ConnectionType
    {
        Long,
        Short
    }
}
