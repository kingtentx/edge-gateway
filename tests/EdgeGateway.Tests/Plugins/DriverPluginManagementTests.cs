using EdgeGateway.Abstractions;
using EdgeGateway.Application;
using EdgeGateway.DataAccess;
using EdgeGateway.Driver.Simulator;
using EdgeGateway.Driver.Modbus;
using EdgeGateway.IotGateway;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.Plugins;

public sealed class DriverPluginManagementTests
{
    private const string Id = "test-dynamic-plugin";
    private static string V1 => Path.Combine(AppContext.BaseDirectory, "EdgeGateway.Driver.TestPlugin.dll");
    private static string V2 => Path.Combine(AppContext.BaseDirectory, "plugin-v2", "EdgeGateway.Driver.TestPlugin.dll");

    [Fact]
    public async Task UpdateReplacesFactoryAndRestartLoadsOnlyNewRevision()
    {
        await using var h = new Harness();
        h.Registry.InstallPlugin(V1);
        var original = h.Registry.GetFactory(Id);
        h.Registry.UpdatePlugin(Id, V2, new());
        Assert.NotSame(original, h.Registry.GetFactory(Id));
        Assert.Equal("Dynamic plugin fixture v2", h.Registry.GetFactory(Id).Descriptor.Name);
        Assert.Equal("2.0.0.0", Assert.Single(await h.Sqlite.GetDriverPluginsAsync()).Version);
        var restarted = h.Restart();
        Assert.Equal("2.0.0.0", Assert.Single(restarted.GetInstalledPlugins()).Version);
        Assert.Single(restarted.Drivers, driver => driver.Id == Id);
        await h.Sqlite.SaveAsync(new() { Name = "shared connection still open" });
        Assert.Equal("shared connection still open", (await h.Sqlite.LoadAsync()).Name);
    }

    [Fact]
    public async Task UpdateRejectsDuplicateInstallWrongIdentityAndNonIncreasingVersion()
    {
        await using var h = new Harness();
        h.Registry.InstallPlugin(V1);
        Assert.Throws<InvalidOperationException>(() => h.Registry.InstallPlugin(V2));
        Assert.Throws<InvalidDataException>(() => h.Registry.UpdatePlugin(Id, V1, new()));
        Assert.Throws<InvalidDataException>(() => h.Registry.UpdatePlugin(Id, typeof(SimulatorDriverFactory).Assembly.Location, new()));
        Assert.Equal("1.0.0.0", Assert.Single(h.Restart().GetInstalledPlugins()).Version);
    }

    [Fact]
    public async Task IncompatibleDeviceConfigurationLeavesOriginalFactoryAndManifest()
    {
        await using var h = new Harness();
        h.Registry.InstallPlugin(V1);
        var original = h.Registry.GetFactory(Id);
        var configuration = new GatewayConfiguration
        {
            Devices = [new() { DriverId = Id, Enabled = false, Properties = new() { ["reject-v2"] = "true" } }]
        };
        var error = Assert.Throws<InvalidDataException>(() => h.Registry.UpdatePlugin(Id, V2, configuration));
        Assert.Contains("rejects", error.Message);
        Assert.Same(original, h.Registry.GetFactory(Id));
        Assert.Equal("1.0.0.0", Assert.Single(h.Restart().GetInstalledPlugins()).Version);
    }

    [Fact]
    public async Task FailedMetadataUpdateRollsBackActivationAndLiveFactory()
    {
        await using var h = new Harness();
        h.Registry.InstallPlugin(V1);
        var original = h.Registry.GetFactory(Id);
        h.Store.FailSave = true;
        Assert.Throws<IOException>(() => h.Registry.UpdatePlugin(Id, V2, new()));
        Assert.Same(original, h.Registry.GetFactory(Id));
        h.Store.FailSave = false;
        Assert.Equal("1.0.0.0", Assert.Single(h.Restart().GetInstalledPlugins()).Version);
        h.Registry.UpdatePlugin(Id, V2, new());
        Assert.Equal("2.0.0.0", Assert.Single(h.Registry.GetInstalledPlugins()).Version);
    }

    [Fact]
    public async Task FailedFirstInstallDoesNotBecomeActiveOnRestart()
    {
        await using var h = new Harness();
        h.Store.FailSave = true;
        Assert.Throws<IOException>(() => h.Registry.InstallPlugin(V1));
        Assert.Empty(h.Registry.GetInstalledPlugins());
        h.Store.FailSave = false;
        Assert.Empty(h.Restart().GetInstalledPlugins());
        h.Registry.InstallPlugin(V1);
        Assert.Single(h.Registry.GetInstalledPlugins());
    }

    [Fact]
    public async Task UninstallRemovesLiveAndPersistedEntriesAndRecoversTombstoneAfterRestart()
    {
        await using var h = new Harness();
        h.Registry.InstallPlugin(V1);
        var installedDll = Directory.GetFiles(h.Plugins, "*.dll", SearchOption.AllDirectories).Single();
        var originalBytes = await File.ReadAllBytesAsync(installedDll);
        var installed = Assert.Single(h.Registry.GetInstalledPlugins());
        h.Registry.UninstallPlugin(Id);
        Assert.Empty(h.Registry.GetInstalledPlugins());
        Assert.DoesNotContain(h.Registry.Drivers, driver => driver.Id == Id);
        Assert.Empty(await h.Sqlite.GetDriverPluginsAsync());
        // Simulate stopping after the durable tombstone but before SQLite metadata cleanup.
        await h.Sqlite.SaveDriverPluginAsync(installed);
        Assert.Empty(h.Restart().GetInstalledPlugins());
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(installedDll));
        Assert.Empty(await h.Sqlite.GetDriverPluginsAsync());
        Assert.Contains(h.Registry.Drivers, driver => driver.Id == "simulator");
        await h.Sqlite.SaveAsync(new());
        Assert.Equal(3, (await h.Sqlite.LoadAsync()).SchemaVersion);
    }

    [Fact]
    public async Task UninstallFailureKeepsPluginActiveAndCanBeRetried()
    {
        await using var h = new Harness();
        h.Registry.InstallPlugin(V1);
        h.Store.FailDelete = true;
        Assert.Throws<IOException>(() => h.Registry.UninstallPlugin(Id));
        Assert.Contains(h.Registry.Drivers, driver => driver.Id == Id);
        Assert.Single(h.Restart().GetInstalledPlugins());
        h.Store.FailDelete = false;
        h.Registry.UninstallPlugin(Id);
        Assert.Empty(h.Restart().GetInstalledPlugins());
    }

    [Fact]
    public async Task UninstalledPluginCanBeInstalledAgainWithoutOldTombstoneWinning()
    {
        await using var h = new Harness();
        h.Registry.InstallPlugin(V1);
        h.Registry.UpdatePlugin(Id, V2, new());
        h.Registry.UninstallPlugin(Id);
        var restarted = h.Restart();
        restarted.InstallPlugin(V1);
        Assert.Equal("1.0.0.0", Assert.Single(h.Restart().GetInstalledPlugins()).Version);
    }

    [Fact]
    public async Task MissingDllStillHasManageableRecordAndCanBeUninstalled()
    {
        await using var h = new Harness();
        h.Registry.InstallPlugin(V1);
        var dll = Directory.GetFiles(h.Plugins, "*.dll", SearchOption.AllDirectories).Single();
        File.Delete(dll);
        var restarted = h.Restart();
        Assert.DoesNotContain(restarted.Drivers, driver => driver.Id == Id);
        Assert.Single(restarted.GetInstalledPlugins());
        restarted.UninstallPlugin(Id);
        Assert.Empty(h.Restart().GetInstalledPlugins());
    }

    [Fact]
    public async Task BundledDriverDeletionPersistsAndOriginalDllCanBeReinstalled()
    {
        await using var h = new Harness();
        h.Registry.UninstallPlugin("simulator");
        Assert.DoesNotContain(h.Registry.Drivers, driver => driver.Id == "simulator");
        var restarted = h.Restart();
        Assert.DoesNotContain(restarted.Drivers, driver => driver.Id == "simulator");
        restarted.InstallPlugin(typeof(SimulatorDriverFactory).Assembly.Location);
        Assert.Contains(h.Restart().Drivers, driver => driver.Id == "simulator");
    }

    [Fact]
    public async Task ApplicationEnforcesUsageRulesAndPreservesUnrelatedRunningDevice()
    {
        await using var h = new Harness();
        await using var messages = new FileGatewayStore(Path.Combine(h.Root, "messages"));
        await messages.InitializeAsync();
        await using var publisher = new NoNetworkPublisher();
        await using var app = new GatewayApplication(new(), h.Registry, h.Sqlite, messages, publisher, h.Log, new JsonConfigurationFiles());
        await app.InstallDriverPluginAsync(V1);
        var uploaded = new DeviceConfiguration { Name = "uploaded device", DriverId = Id };
        var other = new DeviceConfiguration { Name = "unrelated device", DriverId = "simulator" };
        await app.AddDeviceAsync(uploaded);
        await app.AddDeviceAsync(other);
        await app.StartDeviceAsync(other.Id);
        await app.StartDeviceAsync(uploaded.Id);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.UpdateDriverPluginAsync(Id, V2));
        Assert.Contains("停止", error.Message);
        var detail = await app.GetDriverDetailsAsync(Id);
        Assert.True(Assert.Single(detail.Devices).IsRunning);
        await app.StopDeviceAsync(uploaded.Id);
        await app.UpdateDriverPluginAsync(Id, V2);
        Assert.True(app.IsDeviceRunning(other.Id));
        Assert.False(app.IsDeviceRunning(uploaded.Id));
        Assert.Equal("2.0.0.0", (await app.GetDriverDetailsAsync(Id)).Driver.Version);
        uploaded.Enabled = false;
        await app.UpdateDeviceAsync(uploaded);
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.UninstallDriverPluginAsync(Id));
        Assert.Contains("引用", error.Message);
        await app.DeleteDeviceAsync(uploaded.Id);
        await app.UninstallDriverPluginAsync(Id);
        Assert.True(app.IsDeviceRunning(other.Id));
        Assert.DoesNotContain(await app.GetDriverListAsync(), driver => driver.Id == Id);
    }

    [Fact]
    public async Task BundledSharedAssemblyUpdateAndDeleteAffectBothDriversAcrossRestart()
    {
        await using var h = new Harness(nativeModbus: true);
        Assert.Equal(2, h.Registry.GetRelatedDriverIds("modbus-tcp").Count);
        h.Registry.UpdatePlugin("modbus-tcp", Path.Combine(AppContext.BaseDirectory, "plugin-v2", "EdgeGateway.Driver.Modbus.dll"), new());
        Assert.Equal(2, h.Registry.GetInstalledPlugins().Count);
        Assert.All(h.Restart().GetInstalledPlugins(), plugin => Assert.Equal("2.0.0.0", plugin.Version));
        h.Registry.UninstallPlugin("modbus-rtu");
        var restarted = h.Restart();
        Assert.DoesNotContain(restarted.Drivers, driver => driver.Id.StartsWith("modbus-"));
        Assert.Contains(restarted.Drivers, driver => driver.Id == "simulator");
    }

    [Fact]
    public async Task SharedAssemblyDeletionChecksReferencesToSiblingDriver()
    {
        await using var h = new Harness(nativeModbus: true);
        await using var messages = new FileGatewayStore(Path.Combine(h.Root, "messages"));
        await messages.InitializeAsync();
        await using var publisher = new NoNetworkPublisher();
        await using var app = new GatewayApplication(new(), h.Registry, h.Sqlite, messages, publisher, h.Log, new JsonConfigurationFiles());
        await app.AddDeviceAsync(new() { Name = "RTU reference", DriverId = "modbus-rtu", Enabled = false });
        var details = await app.GetDriverDetailsAsync("modbus-tcp");
        Assert.Equal(2, details.RelatedDriverIds.Count);
        Assert.Equal("RTU reference", Assert.Single(details.Devices).Name);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.UninstallDriverPluginAsync("modbus-tcp"));
        Assert.Contains("RTU reference", error.Message);
        Assert.Contains(h.Registry.Drivers, driver => driver.Id == "modbus-tcp");
    }

    [Fact]
    public async Task MigratedDriverAcceptsOriginalInterfaceDllUpdateAndPersistsDeletion()
    {
        await using var h = new Harness(legacyTcp: true);
        const string id = "Mock.TcpClient";
        h.Registry.UpdatePlugin(id, Path.Combine(AppContext.BaseDirectory, "plugin-v2", id + ".dll"), new());
        Assert.True(h.Registry.GetFactory(id).Descriptor.IsLegacy);
        Assert.Equal("2.0.0.0", Assert.Single(h.Restart().GetInstalledPlugins()).Version);
        h.Registry.UninstallPlugin(id);
        Assert.DoesNotContain(h.Restart().Drivers, driver => driver.Id == id);
    }

    [Fact]
    public async Task SharedAssemblyMetadataFailurePreservesBothOldFactories()
    {
        await using var h = new Harness(nativeModbus: true);
        var tcp = h.Registry.GetFactory("modbus-tcp");
        var rtu = h.Registry.GetFactory("modbus-rtu");
        h.Store.FailSave = true;
        Assert.Throws<IOException>(() => h.Registry.UpdatePlugin("modbus-rtu",
            Path.Combine(AppContext.BaseDirectory, "plugin-v2", "EdgeGateway.Driver.Modbus.dll"), new()));
        Assert.Same(tcp, h.Registry.GetFactory("modbus-tcp"));
        Assert.Same(rtu, h.Registry.GetFactory("modbus-rtu"));
        h.Store.FailSave = false;
        Assert.Equal(3, h.Restart().Drivers.Count);
    }

    [Fact]
    public async Task InvalidBundledUpdateDoesNotSilentlyFallBackToShippedVersion()
    {
        await using var h = new Harness(nativeModbus: true);
        h.Registry.UpdatePlugin("modbus-tcp", Path.Combine(AppContext.BaseDirectory, "plugin-v2", "EdgeGateway.Driver.Modbus.dll"), new());
        var dll = Directory.GetFiles(h.Plugins, "*.dll", SearchOption.AllDirectories).Single();
        File.Delete(dll);
        var restarted = h.Restart();
        Assert.DoesNotContain(restarted.Drivers, driver => driver.Id.StartsWith("modbus-"));
        Assert.Equal(2, restarted.GetInstalledPlugins().Count);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "edge-plugin-crud-" + Guid.NewGuid().ToString("N"));
        public string Plugins => Path.Combine(Root, "plugins");
        public GatewayLog Log { get; } = new();
        public SqliteConfigurationStore Sqlite { get; }
        public FailingPluginStore Store { get; }
        public DriverRegistry Registry { get; }
        private readonly bool _nativeModbus;
        private readonly bool _legacyTcp;
        public Harness(bool nativeModbus = false, bool legacyTcp = false)
        {
            _nativeModbus = nativeModbus;
            _legacyTcp = legacyTcp;
            Directory.CreateDirectory(Root);
            Sqlite = new(Path.Combine(Root, "configuration.db"));
            Store = new(Sqlite);
            Registry = Restart();
        }
        public DriverRegistry Restart()
        {
            var factories = new List<IDriverFactory> { new SimulatorDriverFactory() };
            if (_nativeModbus) factories.AddRange([new ModbusTcpDriverFactory(), new ModbusRtuDriverFactory()]);
            var identities = new Dictionary<string, string>();
            if (_legacyTcp) identities["Mock.TcpClient"] = "Mock.TcpClient";
            var registry = new DriverRegistry(factories, Log, Store, identities, assembly => IotGatewayDriverCatalog.CreateFactories(assembly, Log));
            registry.LoadPlugins(Plugins);
            if (_legacyTcp && !registry.ManagedDriverIds.Contains("Mock.TcpClient"))
                foreach (var factory in IotGatewayDriverCatalog.CreateFactories(typeof(Mock.TcpClient.DeviceTcpClient).Assembly, Log)) registry.Register(factory);
            return registry;
        }
        public async ValueTask DisposeAsync()
        {
            await Sqlite.DisposeAsync();
            await Log.DisposeAsync();
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FailingPluginStore(IDriverPluginStore inner) : IDriverPluginStore
    {
        public bool FailSave;
        public bool FailDelete;
        public Task SaveDriverPluginAsync(DriverPluginInfo plugin, CancellationToken cancellationToken = default) =>
            FailSave ? Task.FromException(new IOException("controlled plugin save failure")) : inner.SaveDriverPluginAsync(plugin, cancellationToken);
        public Task ReplaceDriverPluginsAsync(IReadOnlyList<string> ids, IReadOnlyList<DriverPluginInfo> plugins, CancellationToken cancellationToken = default) =>
            (plugins.Count > 0 ? FailSave : FailDelete) ? Task.FromException(new IOException("controlled plugin transaction failure"))
                : inner.ReplaceDriverPluginsAsync(ids, plugins, cancellationToken);
        public Task<IReadOnlyList<DriverPluginInfo>> GetDriverPluginsAsync(CancellationToken cancellationToken = default) => inner.GetDriverPluginsAsync(cancellationToken);
    }

    private sealed class NoNetworkPublisher : ITelemetryPublisher
    {
        public bool IsConnected => false;
        public Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
