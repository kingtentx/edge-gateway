using Microsoft.Extensions.Logging;
using PluginInterface;
using S7.Net;
using S7.Net.Types;
using System.Text;

namespace PLC.SiemensS7
{
    [DriverSupported("1500")]
    [DriverSupported("1200")]
    [DriverSupported("400")]
    [DriverSupported("300")]
    [DriverSupported("200")]
    [DriverSupported("200Smart")]
    [DriverInfo("SiemensS7", "V1.0.0", "Copyright IoTGateway.net 20230220")]
    public class DeviceSiemensS7 : IDriver
    {
        private Plc _plc;

        public ILogger _logger { get; set; }
        private readonly string _device;

        #region 配置参数

        [ConfigParameter("设备Id")] public string DeviceId { get; set; }

        [ConfigParameter("PLC类型")] public CpuType CpuType { get; set; } = CpuType.S71200;

        [ConfigParameter("IP地址")] public string IpAddress { get; set; } = "127.0.0.1";

        [ConfigParameter("端口号")] public int Port { get; set; } = 102;

        [ConfigParameter("Rack")] public short Rack { get; set; } = 0;

        [ConfigParameter("Slot")] public short Slot { get; set; } = 0;

        [ConfigParameter("超时时间ms")] public int Timeout { get; set; } = 3000;

        [ConfigParameter("最小通讯周期ms")] public uint MinPeriod { get; set; } = 3000;

        #endregion 配置参数

        #region 生命周期

        public DeviceSiemensS7(string device, ILogger logger)
        {
            _device = device;
            _logger = logger;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _logger.LogInformation($"Device:[{_device}],Create()");
        }

        /// <summary>
        /// 连接状态
        /// </summary>
        public bool IsConnected => _plc is { IsConnected: true };

        /// <summary>
        /// 连接
        /// </summary>
        /// <returns></returns>
        public bool Connect()
        {
            try
            {
                _logger.LogInformation($"Device:[{_device}],Connect()");

                _plc?.Close();
                if (Timeout <= 0) throw new ArgumentOutOfRangeException(nameof(Timeout));
                _plc = new Plc(CpuType, IpAddress, Port, Rack, Slot);
                _plc.ReadTimeout = Timeout;
                _plc.WriteTimeout = Timeout;
                _plc.Open();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Device:[{_device}],Connect(),Error");
                Close();
                return false;
            }

            return IsConnected;
        }

        /// <summary>
        /// 断开
        /// </summary>
        /// <returns></returns>
        public bool Close()
        {
            try
            {
                _logger.LogInformation($"Device:[{_device}],Close()");

                _plc?.Close();
                return !IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Device:[{_device}],Close(),Error");
                return false;
            }
        }

        /// <summary>
        /// 释放
        /// </summary>
        public void Dispose()
        {
            try
            {
                _logger.LogInformation($"Device:[{_device}],Dispose()");

                _plc?.Close();
                _plc = null;

                // Suppress finalization.
                GC.SuppressFinalize(this);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Device:[{_device}],Dispose(),Error");
            }
        }

        #endregion 生命周期

        #region 读写方法

        /// <summary>
        /// 读
        /// </summary>
        /// <param name="ioArg"></param>
        /// <returns></returns>
        [Method("读西门子PLC标准地址", description: "读西门子PLC标准地址")]
        public DriverReturnValueModel Read(DriverAddressIoArgModel ioArg)
        {
            var encodeArr = System.Text.Encoding.GetEncodings();
            var ret = new DriverReturnValueModel { StatusType = VaribaleStatusTypeEnum.Good };

            if (_plc is { IsConnected: true })
            {
                try
                {
                    if (ioArg.ValueType == DataTypeEnum.AsciiString || ioArg.ValueType == DataTypeEnum.Utf8String || ioArg.ValueType == DataTypeEnum.Gb2312String)
                    {
                        var str = string.Empty;
                        var dataItem = S7.Net.Types.DataItem.FromAddress(ioArg.Address);
                        var head = _plc.ReadBytes(dataItem.DataType, dataItem.DB, dataItem.StartByteAdr, 2);
                        if (head.Length != 2 || head[1] > head[0]) throw new IOException("S7 STRING 长度头无效。");
                        var strBytes = _plc.ReadBytes(dataItem.DataType, dataItem.DB, dataItem.StartByteAdr + 2, head[1]);
                        var strRaw = GetString(ioArg.ValueType, strBytes);

                        ret.Value = strRaw;
                    }
                    else
                    {
                        ret.Value = _plc.Read(ioArg.Address);
                        switch (ioArg.ValueType)
                        {
                            case DataTypeEnum.Byte:
                                ret.Value = unchecked((sbyte)(byte)ret.Value);
                                break;
                            case DataTypeEnum.Bit:
                                ret.Value = (bool)ret.Value ? 1 : 0;
                                break;
                            case DataTypeEnum.Int16:
                                ret.Value = (short)(ushort)ret.Value;
                                break;
                            case DataTypeEnum.Int32:
                                ret.Value = (int)(uint)ret.Value;
                                break;
                            case DataTypeEnum.Float:
                                var buffer = new byte[4];
                                buffer[3] = (byte)((uint)ret.Value >> 24);
                                buffer[2] = (byte)((uint)ret.Value >> 16);
                                buffer[1] = (byte)((uint)ret.Value >> 8);
                                buffer[0] = (byte)((uint)ret.Value >> 0);
                                ret.Value = BitConverter.ToSingle(buffer, 0);
                                break;
                            default:
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ret.StatusType = VaribaleStatusTypeEnum.Bad;
                    ret.Message = $"读取失败,{ex.Message}";
                }
            }
            else
            {
                ret.StatusType = VaribaleStatusTypeEnum.Bad;
                ret.Message = "连接失败";
            }

            return ret;
        }

        /// <summary>
        /// 读字符串
        /// </summary>
        /// <param name="ioArg"></param>
        /// <returns></returns>
        [Method("读西门子字节字符串", description: "DB10.DBW6,10  即开始地址，字节长度")]
        public DriverReturnValueModel ReadByteString(DriverAddressIoArgModel ioArg)
        {
            var ret = new DriverReturnValueModel { StatusType = VaribaleStatusTypeEnum.Good };

            if (_plc != null && _plc.IsConnected)
            {
                var str = string.Empty;
                try
                {
                    var arrParams = ioArg.Address.Trim().Split(',');
                    if (arrParams.Length == 2)
                    {
                        var dataItemitem = S7.Net.Types.DataItem.FromAddress(arrParams[0]);
                        int.TryParse(arrParams[1], out var length);

                        var data = _plc.ReadBytes(dataItemitem.DataType, dataItemitem.DB, dataItemitem.StartByteAdr, length);
                        var strRaw = Encoding.ASCII.GetString(data).TrimEnd(new char[] { '\0' });
                        if (strRaw.Any())
                        {
                            foreach (var chart in strRaw)
                            {
                                if (chart >= 0x20 && chart <= 0x7E)
                                    str += chart;
                            }
                        }
                    }
                    else
                        ret.StatusType = VaribaleStatusTypeEnum.AddressError;
                    ret.Value = str;
                }
                catch (Exception ex)
                {
                    ret.StatusType = VaribaleStatusTypeEnum.Bad;
                    ret.Message = $"读取失败,{ex.Message}";
                }
            }
            else
            {
                ret.StatusType = VaribaleStatusTypeEnum.Bad;
                ret.Message = "连接失败";
            }

            return ret;
        }

        /// <summary>
        /// 写
        /// </summary>
        /// <param name="requestId"></param>
        /// <param name="method"></param>
        /// <param name="ioArg"></param>
        /// <returns></returns>
        public async Task<RpcResponse> WriteAsync(string requestId, string method, DriverAddressIoArgModel ioArg)
        {
            RpcResponse rpcResponse = new() { IsSuccess = false };
            try
            {
                if (!IsConnected)
                    rpcResponse.Description = "设备连接已断开";
                else
                {
                    object? toWrite = null;
                    switch (ioArg.ValueType)
                    {
                        case DataTypeEnum.Bit:
                        case DataTypeEnum.Bool:
                            toWrite = ioArg.Value?.ToString()?.Trim().ToLowerInvariant() switch
                            {
                                "true" or "1" => true,
                                "false" or "0" => false,
                                _ => throw new FormatException("Boolean 值必须为 true/false 或 1/0。"),
                            };
                            break;

                        case DataTypeEnum.UByte:
                            toWrite = byte.Parse(ioArg.Value.ToString());
                            break;

                        case DataTypeEnum.Byte:
                            toWrite = unchecked((byte)sbyte.Parse(ioArg.Value.ToString()));
                            break;

                        case DataTypeEnum.Uint16:
                            toWrite = ushort.Parse(ioArg.Value.ToString());
                            break;

                        case DataTypeEnum.Int16:
                            toWrite = short.Parse(ioArg.Value.ToString());
                            break;

                        case DataTypeEnum.Uint32:
                            toWrite = uint.Parse(ioArg.Value.ToString());
                            break;

                        case DataTypeEnum.Int32:
                            toWrite = int.Parse(ioArg.Value.ToString());
                            break;

                        case DataTypeEnum.Float:
                            toWrite = float.Parse(ioArg.Value.ToString());
                            break;

                        case DataTypeEnum.AsciiString:
                            toWrite = GetStringBytes(ioArg);
                            break;

                        default:
                            rpcResponse.Description = $"类型{ioArg.ValueType}不支持写入";
                            break;
                    }

                    if (toWrite == null)
                    {
                        rpcResponse.Description = "解析错误";
                        return rpcResponse;
                    }

                    //通用方法
                    if (method == nameof(Read))
                    {
                        var dataItem = DataItem.FromAddress(ioArg.Address);
                        if (ioArg.ValueType == DataTypeEnum.AsciiString)
                        {
                            //先写入长度
                            await _plc?.WriteAsync(dataItem.DataType, dataItem.DB, dataItem.StartByteAdr + 1,
                               (byte)((byte[])toWrite).Length);
                            //在写入字符串内容
                            await _plc?.WriteAsync(dataItem.DataType, dataItem.DB, dataItem.StartByteAdr + 2, (byte[])toWrite);
                        }
                        else
                            await _plc?.WriteAsync(ioArg.Address, toWrite);

                        rpcResponse.IsSuccess = true;
                        return rpcResponse;
                    }
                    //数组字符串
                    if (method == nameof(ReadByteString))
                    {
                        var arrParams = ioArg.Address.Trim().Split(',');
                        if (arrParams.Length == 2)
                        {
                            var dataItem = DataItem.FromAddress(arrParams[0]);
                            await _plc?.WriteAsync(dataItem.DataType, dataItem.DB, dataItem.StartByteAdr, toWrite);
                            rpcResponse.IsSuccess = true;
                            return rpcResponse;
                        }
                        else
                            rpcResponse.Description = $"地址错误:{method}";
                    }
                    else
                        rpcResponse.Description = $"不支持写入:{method}";
                }
            }
            catch (Exception ex)
            {
                rpcResponse.Description = $"写入失败,[method]:{method},[ioArg]:{ioArg},[ex]:{ex}";
            }

            return rpcResponse;
        }

        #endregion 读写方法

        #region 私有方法

        private byte[]? GetStringBytes(DriverAddressIoArgModel ioArg)
        {
            string value = ioArg.Value?.ToString() ?? throw new FormatException("写入字符串不能为空引用。");
            Encoding encoding = ioArg.ValueType switch
            {
                DataTypeEnum.Utf8String => new UTF8Encoding(false, true),
                DataTypeEnum.Gb2312String => Encoding.GetEncoding("gb2312", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
                _ => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            };
            byte[] encoded = encoding.GetBytes(value);
            var parameters = ioArg.Address.Trim().Split(',');
            int capacity;
            bool fixedLength = parameters.Length == 2;
            if (fixedLength)
            {
                if (!int.TryParse(parameters[1], out capacity) || capacity <= 0 || capacity > 65535)
                    throw new FormatException("字节字符串长度必须为 1～65535。");
            }
            else
            {
                if (parameters.Length != 1) throw new FormatException("S7 字符串地址无效。");
                var dataItem = DataItem.FromAddress(ioArg.Address);
                byte[] header = _plc.ReadBytes(dataItem.DataType, dataItem.DB, dataItem.StartByteAdr, 2);
                if (header.Length != 2 || header[0] > 254 || header[1] > header[0])
                    throw new IOException("S7 STRING 长度头无效。");
                capacity = header[0];
            }
            if (encoded.Length > capacity)
                throw new FormatException($"编码后字符串长 {encoded.Length} 字节，超过点位容量 {capacity}。");
            if (!fixedLength) return encoded;
            byte[] padded = new byte[capacity];
            encoded.CopyTo(padded, 0);
            return padded;
        }

        private string GetString(DataTypeEnum dataType, byte[] strBytes)
        {
            string? str = string.Empty;
            switch (dataType)
            {
                case DataTypeEnum.Utf8String:
                    str = Encoding.UTF8.GetString(strBytes);
                    break;

                case DataTypeEnum.Gb2312String:
                    Encoding fromEncoding = Encoding.GetEncoding("gb2312");
                    byte[] toBytes = Encoding.Convert(fromEncoding, Encoding.UTF8, strBytes);
                    str = Encoding.UTF8.GetString(toBytes);
                    break;

                case DataTypeEnum.AsciiString:
                default:
                    var strRaw = Encoding.ASCII.GetString(strBytes.Where(x => x is >= 0x20 and <= 0x7E).ToArray());
                    break;
            }

            return str.TrimEnd(new char[] { '\0' });
        }

        #endregion 私有方法
    }
}