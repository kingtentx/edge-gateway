using EdgeGateway.Abstractions;
using EdgeGateway.Application;
using EdgeGateway.DataAccess;
using EdgeGateway.Driver.Modbus;
using EdgeGateway.Driver.Simulator;
using EdgeGateway.Infrastructure.Mqtt;
using EdgeGateway.IotGateway;
using EdgeGateway.Service;

namespace EdgeGateway.Hosting;

/// <summary>
/// Composition root for the desktop host. Opening a host loads and validates configuration,
/// but never starts acquisition; AutoStart is an explicit application command after UI startup.
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly ITelemetryPublisher _publisher;
    private readonly IGatewayStore _store;
    private readonly FileStream _directoryLock;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    private GatewayHost(string directory, IGatewayApplication application, ITelemetryPublisher publisher,
        IGatewayStore store, FileStream directoryLock)
    {
        DataDirectory = directory;
        Application = application;
        _publisher = publisher;
        _store = store;
        _directoryLock = directoryLock;
    }

    public string DataDirectory { get; }
    public IGatewayApplication Application { get; }

    public static async Task<GatewayHost> CreateAsync(string dataDirectory, string? pluginDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(dataDirectory);
        FileStream? directoryLock = null;
        SqliteGatewayStore? store = null;
        MqttTelemetryPublisher? publisher = null;
        GatewayApplication? application = null;
        try
        {
            Directory.CreateDirectory(directory);
            try
            {
                directoryLock = new FileStream(Path.Combine(directory, "gateway.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("数据目录已被其他采集网关进程使用，请关闭对应进程后重试。", exception);
            }
            var configurationPath = Path.Combine(directory, "gateway.json");
            var persisted = File.Exists(configurationPath);
            var configurations = new JsonConfigurationStore(configurationPath, Service.DemoConfiguration.Create);
            var configuration = await configurations.LoadAsync(cancellationToken).ConfigureAwait(false);
            store = new SqliteGatewayStore(Path.Combine(directory, "gateway.db"));
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var log = new GatewayLog(Path.Combine(directory, "logs"));
            IDriverFactory[] builtInDrivers = [new SimulatorDriverFactory(), new ModbusTcpDriverFactory(), new ModbusRtuDriverFactory()];
            var drivers = new DriverRegistry(builtInDrivers.Concat(IotGatewayDriverCatalog.CreateFactories(log)), log);
            drivers.LoadPlugins(pluginDirectory ?? Path.Combine(AppContext.BaseDirectory, "plugins"));
            cancellationToken.ThrowIfCancellationRequested();
            publisher = new MqttTelemetryPublisher();
            application = new GatewayApplication(configuration, drivers, configurations, store, publisher, log,
                new JsonConfigurationFiles(), persisted);
            return new(directory, application, publisher, store, directoryLock);
        }
        catch
        {
            // Preserve the startup error, but release every resource already acquired so
            // correcting an invalid configuration does not require killing the process.
            if (application is not null) { try { await application.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (publisher is not null) { try { await publisher.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (store is not null) { try { await store.DisposeAsync().ConfigureAwait(false); } catch { } }
            try { directoryLock?.Dispose(); } catch { }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception> errors = [];
        try { await Application.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
        try { await _publisher.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
        try { await _store.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
        try { _directoryLock.Dispose(); } catch (Exception exception) { errors.Add(exception); }
        if (errors.Count > 0) throw new AggregateException("释放采集网关资源时发生错误。", errors);
    }
}
