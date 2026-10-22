using Microsoft.Extensions.Logging;
using PluginInterface;
using SimpleTCP;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace Other.Toledo
{
    [DriverSupported("Toledo")]
    [DriverInfo("Toledo", "V1.0.0", "Copyright IoTGateway.net 20230220")]
    public class DeviceToledo : IDriver
    {
        private SimpleTcpClient? client;
        private byte[]? latestRcvData;
        private DateTime latestDate;
        private readonly object _receiveGate = new();
        private readonly List<byte> _pendingBytes = new();
        private readonly ManualResetEventSlim _frameReady = new(false);
        private bool _waitingForFrame;
        public ILogger _logger { get; set; }
        private readonly string _device;

        [ConfigParameter("设备Id")] public string DeviceId { get; set; }
        [ConfigParameter("IP地址")]
        public string IpAddress { get; set; } = "127.0.0.1";
        [ConfigParameter("端口号")]
        public int Port { get; set; } = 6666;
        // Original property name retained: here it is the TCP frame length, not UART bits.
        [ConfigParameter("数据位")]
        public int DataBits { get; set; } = 17;
        // Original serial settings are retained for metadata compatibility; this driver uses TCP.
        [ConfigParameter("校验位")]
        public Parity Parity { get; set; } = Parity.None;
        [ConfigParameter("停止位")]
        public StopBits StopBits { get; set; } = StopBits.One;
        [ConfigParameter("连接类型")]
        public ConnectionType ConnectionType { get; set; } = ConnectionType.Long;
        [ConfigParameter("超时时间ms")]
        public int Timeout { get; set; } = 300;
        [ConfigParameter("最小通讯周期ms")]
        public uint MinPeriod { get; set; } = 3000;

        public DeviceToledo(string device, ILogger logger)
        {
            _device = device;
            _logger = logger;
        }

        public bool IsConnected => client?.TcpClient?.Connected == true;

        public bool Connect()
        {
            try
            {
                Close();
                if (Timeout <= 0 || DataBits < 3 || DataBits > 4096)
                    throw new ArgumentOutOfRangeException(nameof(DataBits), "TCP 帧长度必须为 3～4096 字节，超时必须大于 0。");
                client = new SimpleTcpClient();
                client.DataReceived += Client_DataReceived;
                client.Connect(IpAddress, Port);
                client.TcpClient.SendTimeout = Timeout;
                return IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device:[{Device}], Toledo TCP 连接失败", _device);
                Close();
                return false;
            }
        }

        public bool Close()
        {
            var oldClient = client;
            client = null;
            lock (_receiveGate)
            {
                _waitingForFrame = false;
                latestRcvData = null;
                latestDate = default;
                _pendingBytes.Clear();
                _frameReady.Set();
            }
            try
            {
                if (oldClient != null)
                {
                    oldClient.DataReceived -= Client_DataReceived;
                    try { oldClient.Disconnect(); }
                    finally { oldClient.Dispose(); }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device:[{Device}], Toledo TCP 连接关闭失败", _device);
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
            var ret = AnalyseAddress(ioArg, out ushort address, out ushort count);
            if (ret.StatusType != VaribaleStatusTypeEnum.Good) return ret;
            try
            {
                if (!IsConnected) throw new IOException("连接失败。");
                lock (_receiveGate)
                {
                    latestRcvData = null;
                    latestDate = default;
                    _pendingBytes.Clear();
                    _frameReady.Reset();
                    _waitingForFrame = true;
                }
                if (!_frameReady.Wait(Timeout))
                    throw new TimeoutException("超时未收到新的完整称重帧。");
                byte[] frame;
                DateTime frameTime;
                lock (_receiveGate)
                {
                    frame = latestRcvData?.ToArray() ?? throw new IOException("连接关闭或称重帧无效。");
                    frameTime = latestDate;
                }
                if (address + count > frame.Length) throw new FormatException("点位地址超出完整称重帧。");
                var bytes = frame.AsSpan(address, count);
                ret.Value = ioArg.ValueType switch
                {
                    DataTypeEnum.UByte => (object)bytes[0],
                    DataTypeEnum.Byte => unchecked((sbyte)bytes[0]),
                    DataTypeEnum.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
                    DataTypeEnum.Int32 => int.Parse(Encoding.ASCII.GetString(bytes).Replace("\0", "").Trim(), CultureInfo.InvariantCulture),
                    DataTypeEnum.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
                    DataTypeEnum.AsciiString => Encoding.ASCII.GetString(bytes).Replace("\0", ""),
                    DataTypeEnum.Custome1 => frameTime,
                    _ => throw new NotSupportedException("该称重协议不支持此数据类型。"),
                };
            }
            catch (Exception ex)
            {
                ret.StatusType = VaribaleStatusTypeEnum.Bad;
                ret.Message = $"读取失败，{ex.Message}";
            }
            finally
            {
                lock (_receiveGate) _waitingForFrame = false;
            }
            return ret;
        }

        public Task<RpcResponse> WriteAsync(string requestId, string method, DriverAddressIoArgModel ioArg)
            => Task.FromResult(new RpcResponse { IsSuccess = false, Description = "设备驱动内未实现写入功能" });

        private void Client_DataReceived(object? sender, Message e)
        {
            lock (_receiveGate)
            {
                if (!_waitingForFrame) return;
                foreach (byte value in e.Data)
                {
                    if (_pendingBytes.Count == 0 && value != 0x02) continue;
                    _pendingBytes.Add(value);
                    if (_pendingBytes.Count < DataBits) continue;
                    if (_pendingBytes[DataBits - 1] == 0x0d)
                    {
                        latestRcvData = _pendingBytes.ToArray();
                        latestDate = DateTime.UtcNow;
                        _pendingBytes.Clear();
                        _waitingForFrame = false;
                        _frameReady.Set();
                        break;
                    }
                    // Recover alignment while keeping at most one bounded partial frame.
                    int nextStart = _pendingBytes.FindIndex(1, x => x == 0x02);
                    if (nextStart >= 0) _pendingBytes.RemoveRange(0, nextStart);
                    else _pendingBytes.Clear();
                }
            }
        }

        private DriverReturnValueModel AnalyseAddress(DriverAddressIoArgModel ioarg, out ushort StartAddress, out ushort ReadCount)
        {
            var ret = new DriverReturnValueModel { StatusType = VaribaleStatusTypeEnum.Good };
            StartAddress = ReadCount = 0;
            try
            {
                var parts = ioarg.Address.Split(',');
                if (parts.Length < 1 || parts.Length > 2) throw new FormatException("地址格式应为偏移或偏移,长度。");
                StartAddress = ushort.Parse(parts[0], CultureInfo.InvariantCulture);
                ReadCount = parts.Length == 2 ? ushort.Parse(parts[1], CultureInfo.InvariantCulture) : (ushort)(ioarg.ValueType switch
                {
                    DataTypeEnum.Int16 => 2,
                    DataTypeEnum.Float => 4,
                    DataTypeEnum.UByte or DataTypeEnum.Byte or DataTypeEnum.Custome1 => 1,
                    DataTypeEnum.Int32 or DataTypeEnum.AsciiString => throw new FormatException("ASCII 数字/字符串必须指定偏移,长度。"),
                    _ => throw new NotSupportedException("该称重协议不支持此数据类型。"),
                });
                if (ReadCount == 0 || StartAddress + ReadCount > DataBits)
                    throw new FormatException("地址和长度超出 TCP 帧范围。");
                if ((ioarg.ValueType is DataTypeEnum.UByte or DataTypeEnum.Byte or DataTypeEnum.Custome1) && ReadCount != 1 ||
                    ioarg.ValueType == DataTypeEnum.Int16 && ReadCount != 2 ||
                    ioarg.ValueType == DataTypeEnum.Float && ReadCount != 4)
                    throw new FormatException("长度与数据类型不匹配。");
            }
            catch (Exception ex)
            {
                ret.StatusType = VaribaleStatusTypeEnum.AddressError;
                ret.Message = ex.Message;
            }
            return ret;
        }
    }

    public enum ConnectionType
    {
        Long,
        Short
    }
}
