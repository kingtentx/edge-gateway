using Microsoft.Extensions.Logging;
using OpenNETCF.MTConnect;
using PluginInterface;
using System.Xml.Linq;

namespace CNC.MTConnect
{
    [DriverSupported("MTConnectClient")]
    [DriverInfo("MTConnectClient", "V1.0.0", "Copyright IoTGateway.net 20230220")]
    public class DeviceMTClient : IDriver
    {
        private EntityClient? _mClient;
        public ILogger _logger { get; set; }
        private readonly string _device;

        #region 配置参数

        [ConfigParameter("设备Id")] public string DeviceId { get; set; }

        [ConfigParameter("uri")] public string Uri { get; set; }

        [ConfigParameter("超时时间ms")] public int Timeout { get; set; } = 3000;

        [ConfigParameter("最小通讯周期ms")] public uint MinPeriod { get; set; } = 3000;

        public bool IsConnected { get; set; }

        #endregion 配置参数

        #region 生命周期

        public DeviceMTClient(string device, ILogger logger)
        {
            _device = device;
            _logger = logger;

            _logger.LogInformation($"Device:[{_device}],Create()");
        }

        public bool Close()
        {
            try
            {
                _mClient = null;
                IsConnected = false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool Connect()
        {
            try
            {
                Close();
                if (Timeout <= 0) throw new ArgumentOutOfRangeException(nameof(Timeout));
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Timeout), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
                using var response = http.GetAsync(Uri.TrimEnd('/') + "/probe").GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var xml = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (XDocument.Parse(xml).Root?.Name.LocalName != "MTConnectDevices")
                    throw new IOException("MTConnect /probe 未返回 MTConnectDevices 文档。");
                _mClient = new EntityClient(Uri);
                _mClient.RequestTimeout = Timeout;
                IsConnected = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MTConnect /probe 连接验证失败");
                IsConnected = false;
            }

            return IsConnected;
        }

        public void Dispose()
        {
            Close();
            // Suppress finalization.
            GC.SuppressFinalize(this);
        }

        #endregion 生命周期

        #region 读写方法

        [Method("读MTConnect", description: "读MTConnect ID")]
        public DriverReturnValueModel ReadById(DriverAddressIoArgModel ioarg)
        {
            var ret = new DriverReturnValueModel { StatusType = VaribaleStatusTypeEnum.Good };

            if (IsConnected)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(ioarg.Address))
                        throw new FormatException("MTConnect 点位 ID 不能为空。");
                    var dataItem = _mClient?.GetDataItemById(ioarg.Address);
                    // The original client returns the first item in the filtered response.
                    // Verify its identity even when the agent ignores the requested filter.
                    if (dataItem == null || !string.Equals(dataItem.DataItemID, ioarg.Address, StringComparison.Ordinal))
                        throw new IOException($"MTConnect 未返回请求的点位 ID：{ioarg.Address}。");
                    var dataValue = dataItem.Value;
                    if (dataValue == null || string.Equals(dataValue.ToString(), "UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("MTConnect 点位不存在或当前值为 UNAVAILABLE。");
                    ret.Value = dataValue;
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

        public DriverReturnValueModel Read(DriverAddressIoArgModel ioarg)
        {
            return ReadById(ioarg);
        }

        public Task<RpcResponse> WriteAsync(string requestId, string method, DriverAddressIoArgModel ioarg)
        {
            RpcResponse rpcResponse = new() { IsSuccess = false, Description = "设备驱动内未实现写入功能" };
            return Task.FromResult(rpcResponse);
        }

        #endregion 读写方法
    }
}
