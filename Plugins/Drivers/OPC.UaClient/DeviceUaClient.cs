using Microsoft.Extensions.Logging;
using Opc.Ua;
using OpcUaHelper;
using PluginInterface;

namespace OPC.UaClient
{
    [DriverSupported("OPCUaClient")]
    [DriverInfo("OPCUaClient", "V1.0.0", "Copyright IoTGateway.net 20230220")]
    public class DeviceUaClient : IDriver
    {
        private OpcUaClientHelper? _opcUaClient;
        public ILogger _logger { get; set; }
        private readonly string _device;

        #region 配置参数

        [ConfigParameter("设备Id")] public string DeviceId { get; set; }

        [ConfigParameter("uri")]
        public string Uri { get; set; } = "opc.tcp://localhost:62541/Quickstarts/ReferenceServer";

        [ConfigParameter("超时时间ms")] public int Timeout { get; set; } = 3000;

        [ConfigParameter("最小通讯周期ms")] public uint MinPeriod { get; set; } = 3000;

        [ConfigParameter("启用 OPC UA 安全策略")] public bool UseSecurity { get; set; } = true;

        [ConfigParameter("OPC UA PKI 根目录（留空使用当前用户目录）")]
        public string CertificateStorePath { get; set; } = "";

        #endregion 配置参数

        #region 生命周期

        /// <summary>
        /// 反射构造函数
        /// </summary>
        /// <param name="device"></param>
        /// <param name="logger"></param>
        public DeviceUaClient(string device, ILogger logger)
        {
            _device = device;
            _logger = logger;

            _logger.LogInformation($"Device:[{_device}],Create()");
        }

        /// <summary>
        /// 连接状态
        /// </summary>
        public bool IsConnected => _opcUaClient is { Connected: true };

        /// <summary>
        /// 连接
        /// </summary>
        /// <returns></returns>
        public bool Connect()
        {
            try
            {
                _logger.LogInformation($"Device:[{_device}],Connect()");

                Close();
                _opcUaClient = new OpcUaClientHelper(Timeout, CertificateStorePath)
                {
                    UseSecurity = UseSecurity,
                    ReconnectPeriod = 0,
                };
                using var cancellation = new CancellationTokenSource(Timeout);
                _opcUaClient.ConnectServer(Uri, cancellation.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Device:[{_device}],Connect(),Error（证书必须受信任；检查 PKI trusted/certs 和服务器端应用证书信任）");
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

                _opcUaClient?.Disconnect();
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
                Close();
                _opcUaClient = null;

                // Suppress finalization.
                GC.SuppressFinalize(this);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Device:[{_device}],Dispose(),Error");
            }
        }

        #endregion 生命周期

        [Method("读OPCUa", description: "读OPCUa节点")]
        public DriverReturnValueModel ReadNode(DriverAddressIoArgModel ioArg)
        {
            var ret = new DriverReturnValueModel { StatusType = VaribaleStatusTypeEnum.Good };

            if (IsConnected)
            {
                try
                {
                    var dataValue = _opcUaClient?.ReadNode(NodeId.Parse(ioArg.Address));
                    if (dataValue == null || !DataValue.IsGood(dataValue) || dataValue.Value == null)
                        throw new IOException($"OPC UA 节点读取失败：{dataValue?.StatusCode.ToString() ?? "无响应"}");
                    ret.Value = dataValue.Value;
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

        [Method("读取节点兼容别名", description: "调用 ReadNode 读取真实节点值")]
        public DriverReturnValueModel Read(DriverAddressIoArgModel ioArg)
        {
            return ReadNode(ioArg);
        }

        public async Task<RpcResponse> WriteAsync(string requestId, string method, DriverAddressIoArgModel ioArg)
        {
            RpcResponse rpcResponse = new() { IsSuccess = false, Description = "设备驱动内未实现写入功能" };
            await Task.CompletedTask;
            return rpcResponse;
        }
    }
}