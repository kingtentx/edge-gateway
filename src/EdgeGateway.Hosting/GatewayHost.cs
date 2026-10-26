using System.Reflection;
using System.Security.Cryptography;
using EdgeGateway.Abstractions;
using EdgeGateway.Application;
using EdgeGateway.DataAccess;
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
    private readonly SqliteConfigurationStore _configurations;
    private readonly GatewayLog _log;
    private readonly SqliteAcquisitionSpool _acquisitionSpool;
    private readonly bool _verificationFaultsEnabled;
    private readonly FileStream _directoryLock;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    private GatewayHost(string directory, IGatewayApplication application, ITelemetryPublisher publisher,
        IGatewayStore store, SqliteConfigurationStore configurations, GatewayLog log, FileStream directoryLock, bool verificationFaultsEnabled)
    {
        DataDirectory = directory;
        Application = application;
        _publisher = publisher;
        _store = store;
        _configurations = configurations;
        _log = log;
        _acquisitionSpool = acquisitionSpool;
        _verificationFaultsEnabled = verificationFaultsEnabled;
        _directoryLock = directoryLock;
    }

    public string DataDirectory { get; }
    public IGatewayApplication Application { get; }

    public static Task<GatewayHost> CreateAsync(string dataDirectory, string? pluginDirectory = null,
        CancellationToken cancellationToken = default, bool enableVerificationFaults = false, string? runtimeDirectory = null) =>
        CreateCoreAsync(dataDirectory, pluginDirectory, cancellationToken, enableVerificationFaults, runtimeDirectory,
            Path.Combine(AppContext.BaseDirectory, "drivers"));

    internal static async Task<GatewayHost> CreateCoreAsync(string dataDirectory, string? pluginDirectory,
        CancellationToken cancellationToken, bool enableVerificationFaults, string? runtimeDirectory, string packagedDriverDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(dataDirectory);
        var runtimeRoot = Path.GetFullPath(runtimeDirectory ?? AppContext.BaseDirectory);
        FileStream? directoryLock = null;
        FileGatewayStore? store = null;
        SqliteConfigurationStore? configurations = null;
        GatewayLog? log = null;
        SqliteAcquisitionSpool? acquisitionSpool = null;
        MqttTelemetryPublisher? publisher = null;
        GatewayApplication? application = null;
        try
        {
            Directory.CreateDirectory(directory);
            try
            {
                directoryLock = new FileStream(Path.Combine(runtimeRoot, "gateway.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("数据目录已被其他采集网关进程使用，请关闭对应进程后重试。", exception);
            }
            var configurationPath = Path.Combine(directory, "gateway.config.db");
            configurations = new SqliteConfigurationStore(configurationPath, Service.DemoConfiguration.Create);
            var configuration = await configurations.LoadAsync(cancellationToken).ConfigureAwait(false);
            var initializeDefaults = !configurations.HasSavedConfiguration;
            store = new FileGatewayStore(Path.Combine(runtimeRoot, "messaging"));
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            log = new GatewayLog(Path.Combine(runtimeRoot, "logs"));
            log.ApplyConfiguration(configuration.Logging);
            acquisitionSpool = new SqliteAcquisitionSpool(Path.Combine(runtimeRoot, "messaging", "acquisition-spool.db"));
            var packagedDriversDirectory = Path.GetFullPath(packagedDriverDirectory);
            var driversDirectory = Path.GetFullPath(pluginDirectory ?? packagedDriversDirectory);
            Directory.CreateDirectory(packagedDriversDirectory);
            Directory.CreateDirectory(driversDirectory);
            DriverAssemblyResolver.Configure(packagedDriversDirectory);
            if (!string.Equals(packagedDriversDirectory, driversDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                DriverAssemblyResolver.Configure(driversDirectory);
            // Protocol assemblies ship under the executable's drivers directory. Isolated UI
            // smoke runs may use a temporary upload directory without losing bundled drivers.
            var bundledAssemblies = IotGatewayDriverCatalog.DriverAssemblies.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            bundledAssemblies["simulator"] = "EdgeGateway.Driver.Simulator";
            bundledAssemblies["modbus-tcp"] = bundledAssemblies["modbus-rtu"] = "EdgeGateway.Driver.Modbus";
            var drivers = new DriverRegistry([], log, configurations, bundledAssemblies,
                assembly => IotGatewayDriverCatalog.CreateFactories(assembly, log));
            drivers.PrepareManagedPlugins(packagedDriversDirectory, driversDirectory);
            drivers.LoadPlugins(packagedDriversDirectory);
            if (!string.Equals(packagedDriversDirectory, driversDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                drivers.LoadPlugins(driversDirectory);
            foreach (var factory in IotGatewayDriverCatalog.CreateFactories(log, drivers.ManagedDriverIds)) drivers.Register(factory);
            if (initializeDefaults)
            {
                // A fresh SQLite database can coexist with an earlier driver uninstall. Never
                // reintroduce a default device whose driver is unavailable, or reactivate its DLL.
                var available = drivers.Drivers.Select(driver => driver.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var device in configuration.Devices.Where(device => !available.Contains(device.DriverId)).ToArray())
                {
                    configuration.Devices.Remove(device);
                    log.Write(Model.GatewayLogLevel.Warning, $"初始化默认配置时跳过设备 [{device.Name}]：驱动 {device.DriverId} 未安装或已卸载。");
                }
                var errors = Service.ConfigurationValidator.Validate(configuration, drivers);
                if (errors.Count > 0)
                    throw new InvalidDataException("默认配置检查未通过，未保存初始化配置：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
                await configurations.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
            }
            var installedRoot = Path.GetFullPath(Path.Combine(driversDirectory, "installed")) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var uploadedPlugins = drivers.GetInstalledPlugins().Where(plugin => plugin.Status == "已加载")
                .ToDictionary(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase);
            var inventory = drivers.Drivers.Select(descriptor =>
            {
                if (uploadedPlugins.TryGetValue(descriptor.Id, out var uploaded))
                    return new DriverInventoryRecord(descriptor.Id, descriptor.Name, descriptor.Description,
                        descriptor.SupportsWrite, descriptor.IsLegacy, uploaded.AssemblyName, uploaded.Version, uploaded.Sha256,
                        drivers.IsBundledDriver(descriptor.Id) ? "bundled" : "uploaded");
                var factory = drivers.GetFactory(descriptor.Id);
                var assemblyName = descriptor.IsLegacy
                    ? descriptor.Id
                    : factory.GetType().Assembly.GetName().Name ?? descriptor.Id;
                var legacyAssembly = Path.Combine(driversDirectory, assemblyName + ".dll");
                if (!File.Exists(legacyAssembly)) legacyAssembly = Path.Combine(packagedDriversDirectory, assemblyName + ".dll");
                var assemblyPath = descriptor.IsLegacy
                    ? legacyAssembly
                    : factory.GetType().Assembly.Location;
                var fullAssemblyPath = string.IsNullOrWhiteSpace(assemblyPath) ? "" : Path.GetFullPath(assemblyPath);
                var exists = fullAssemblyPath.Length > 0 && File.Exists(fullAssemblyPath);
                var source = exists && fullAssemblyPath.StartsWith(installedRoot, comparison) ? "uploaded" : "bundled";
                var version = "unknown";
                var sha256 = "";
                if (exists)
                {
                    var assemblyIdentity = AssemblyName.GetAssemblyName(fullAssemblyPath);
                    version = assemblyIdentity.Version?.ToString() ?? "unknown";
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullAssemblyPath)));
                }
                return new DriverInventoryRecord(descriptor.Id, descriptor.Name, descriptor.Description,
                    descriptor.SupportsWrite, descriptor.IsLegacy, assemblyName, version, sha256, source);
            }).ToArray();
            await configurations.ReplaceDriverInventoryAsync(inventory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            publisher = new MqttTelemetryPublisher();
            var databases = new ExternalDatabaseConnector();
            application = new GatewayApplication(configuration, drivers, configurations, store, publisher, log,
                new JsonConfigurationFiles(), configurations.HasSavedConfiguration, databases,
                dataSink: new AcquisitionDataService(databases, log, acquisitionSpool),
                serverResources: new ServerResourceMonitor(), workbookFiles: new ExcelConfigurationFiles());
            return new(directory, application, publisher, store, configurations, log, acquisitionSpool, directoryLock, enableVerificationFaults);
        }
        catch
        {
            // Preserve the startup error, but release every resource already acquired so
            // correcting an invalid configuration does not require killing the process.
            if (application is not null) { try { await application.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (publisher is not null) { try { await publisher.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (store is not null) { try { await store.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (acquisitionSpool is not null) { try { await acquisitionSpool.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (configurations is not null) { try { await configurations.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (log is not null) { try { await log.DisposeAsync().ConfigureAwait(false); } catch { } }
            try { directoryLock?.Dispose(); } catch { }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    /// <summary>Real storage failure injection for the isolated WinForms verification host only.</summary>
    public IDisposable BeginConfigurationWriteFailureForVerification()
    {
        if (!_verificationFaultsEnabled) throw new InvalidOperationException("仅显式启用的隔离验证宿主允许注入保存失败。");
        return new ConfigurationWriteFailureScope(Path.Combine(DataDirectory, "gateway.config.db"));
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception> errors = [];
        try { await Application.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
        try { await _publisher.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
        try { await _store.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
        try { await _acquisitionSpool.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
        try { await _configurations.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
        try { await _log.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
        try { _directoryLock.Dispose(); } catch (Exception exception) { errors.Add(exception); }
        if (errors.Count > 0) throw new AggregateException("释放采集网关资源时发生错误。", errors);
    }
}
