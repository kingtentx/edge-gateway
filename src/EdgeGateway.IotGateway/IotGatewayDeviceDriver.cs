using System.Reflection;
using System.Runtime.InteropServices;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using PluginInterface;

namespace EdgeGateway.IotGateway;

internal sealed class IotGatewayDeviceDriver : IDeviceDriver
{
    private static readonly object LeaseLock = new();
    private static readonly Dictionary<Guid, IotGatewayDeviceDriver> ActiveLeases = [];
    private readonly DeviceConfiguration _configuration;
    private readonly IotGatewayDriverFactory _factory;
    private readonly DedicatedDriverExecutor _executor;
    private readonly LegacyLogger _logger;
    private readonly IGatewayLog _log;
    private IDriver? _driver;
    private bool _connectionAttempted;
    private int _disposed;

    public IotGatewayDeviceDriver(DeviceConfiguration configuration, IotGatewayDriverFactory factory, IGatewayLog log)
    {
        _configuration = configuration;
        _factory = factory;
        _log = log;
        _logger = new LegacyLogger(log, configuration.Name);
        lock (LeaseLock)
        {
            if (ActiveLeases.TryGetValue(configuration.Id, out var old) && !old._executor.IsFinished)
                throw new InvalidOperationException("同一设备的上次原驱动调用尚未退出；保持隔离，暂不创建新连接，请等待原调用结束或重启程序。");
            _executor = new DedicatedDriverExecutor("iotgateway-" + configuration.Id.ToString("N"), Cleanup,
                log, ReleaseLease);
            ActiveLeases[configuration.Id] = this;
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _executor.InvokeAsync(() =>
        {
            CheckPrerequisites();
            _driver ??= _factory.CreateRaw(_configuration, _logger);
            _connectionAttempted = true;
            if (!_driver.Connect() || !_driver.IsConnected)
                throw new IOException($"原驱动 {_factory.Descriptor.Id} 连接失败。{_logger.LastError ?? _factory.Descriptor.Prerequisites}");
            return true;
        }, OperationBudget(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var results = new List<TagReadResult>(tags.Count);
        // Preserve configured order: batch/cache producer methods must precede consumers in the same cycle.
        foreach (var tag in tags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await _executor.InvokeAsync(() => ReadOne(tag), OperationBudget(), cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private TagReadResult ReadOne(TagConfiguration tag)
    {
        if (_driver is null || !_driver.IsConnected) throw new IOException("原驱动尚未连接或连接已断开。");
        try
        {
            var method = _factory.GetReadMethod(tag);
            var result = method.Invoke(_driver, [BuildArgument(tag, null)]) as DriverReturnValueModel;
            if (result is null) return new(tag.Id, null, SampleQuality.Bad, "原驱动未返回采集结果。");
            if (result.StatusType != VaribaleStatusTypeEnum.Good)
                return new(tag.Id, null, SampleQuality.Bad, $"{result.StatusType}: {result.Message ?? "原驱动读取失败"}");
            var value = LegacyTypeMapping.NormalizeValue(result.Value);
            if (value is null) return new(tag.Id, null, SampleQuality.Bad, "原驱动返回空值，不能标记为 Good。");
            var metadata = _factory.Descriptor.Methods.First(m => m.Name == method.Name);
            if (metadata.IsDiagnostic)
                return new(tag.Id, value, SampleQuality.Uncertain, "诊断示例方法返回值，不代表现场设备采集。");
            return new(tag.Id, value);
        }
        catch (TargetInvocationException ex)
        {
            return new(tag.Id, null, SampleQuality.Bad, ex.GetBaseException().Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(tag.Id, null, SampleQuality.Bad, ex.GetBaseException().Message);
        }
    }

    public async Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var method = _factory.ResolveWriteMethod(tag);
        if (!tag.Writable || !_factory.Descriptor.Methods.Any(m => m.Name == method && m.SupportsWrite))
            throw new InvalidOperationException("该点位或原驱动方法未启用真实写入。");
        await _executor.InvokeAsync(() =>
        {
            if (_driver is null || !_driver.IsConnected) throw new IOException("原驱动尚未连接或连接已断开。");
            var response = _executor.RunTask(() => _driver.WriteAsync(Guid.NewGuid().ToString("N"), method, BuildArgument(tag, value)));
            if (response is null || !response.IsSuccess) throw new IOException(response?.Description ?? "原驱动未确认写入。");
            return true;
        }, OperationBudget(), cancellationToken).ConfigureAwait(false);
    }

    private static DriverAddressIoArgModel BuildArgument(TagConfiguration tag, object? value) => new()
    {
        ID = tag.Id, Address = tag.Address, Value = value!,
        ValueType = LegacyTypeMapping.ToLegacy(tag.DataType), EndianType = LegacyTypeMapping.ToLegacy(tag)
    };

    private int OperationBudget() => checked(_configuration.TimeoutMs + 1000);

    private void CheckPrerequisites()
    {
        if (_factory.Descriptor.Id == "OPC.DaClient" && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("OPC DA 依赖 Windows COM/DCOM，当前系统不能运行此驱动。");
        if (_factory.Descriptor.Id == "CNC.Fanuc")
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("FOCAS 原驱动需要 Windows x64 进程及 FANUC 官方 FWLIB64.dll。");
            if (!NativeLibrary.TryLoad("FWLIB64.dll", typeof(IotGatewayDeviceDriver).Assembly,
                    DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.SafeDirectories, out var handle))
                throw new DllNotFoundException("无法加载 FANUC FWLIB64.dll；请安装官方 x64 运行库及其依赖，不能用模拟连接替代。");
            NativeLibrary.Free(handle);
        }
    }

    private void Cleanup()
    {
        if (_driver is null) return;
        try
        {
            if (_connectionAttempted && !_driver.Close())
                _log.Write(GatewayLogLevel.Warning, $"原驱动 [{_configuration.Name}] Close 返回失败，将继续 Dispose。");
        }
        finally { _driver.Dispose(); _driver = null; }
    }

    private void ReleaseLease()
    {
        lock (LeaseLock)
            if (ActiveLeases.TryGetValue(_configuration.Id, out var active) && ReferenceEquals(active, this))
                ActiveLeases.Remove(_configuration.Id);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _executor.StopAsync().ConfigureAwait(false);
    }
}
