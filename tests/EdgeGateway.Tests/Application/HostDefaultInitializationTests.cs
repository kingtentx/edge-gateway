using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeGateway.DataAccess;
using EdgeGateway.Driver.Modbus;
using EdgeGateway.Driver.Simulator;
using EdgeGateway.Hosting;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.Application;

public sealed class HostDefaultInitializationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edge-initialization-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "AppData");
    private string Drivers => Path.Combine(_root, "drivers");
    private string Database => Path.Combine(Data, "gateway.config.db");

    [Theory]
    [InlineData("installed")]
    [InlineData("uninstalled")]
    [InlineData("missing")]
    public async Task FreshDefaultsUseAvailableDriversAndPersistBeforeCollection(string state)
    {
        StageDrivers(state);
        Guid[] identities;
        await using (var host = await Start())
        {
            Assert.False(host.Application.IsRunning);
            var config = host.Application.GetConfiguration();
            Assert.Equal(state == "installed" ? 3 : 2, config.Devices.Count);
            Assert.Equal(state == "installed", config.Devices.Any(device => device.DriverId == "simulator"));
            Assert.All(config.Devices, device => Assert.Contains(host.Application.Drivers.Drivers, driver => driver.Id == device.DriverId));
            identities = config.Devices.Select(device => device.Id).ToArray();
            await using var saved = new SqliteConfigurationStore(Database);
            Assert.Equal(identities, (await saved.LoadAsync()).Devices.Select(device => device.Id));
            Assert.True(saved.HasSavedConfiguration);
            Assert.Null(await saved.LoadPreviousAsync());
        }
        await using var reopened = await Start();
        Assert.Equal(identities, reopened.Application.GetConfiguration().Devices.Select(device => device.Id));
        if (state == "uninstalled")
        {
            Assert.True(File.Exists(Path.Combine(Drivers, "EdgeGateway.Driver.Simulator.dll")));
            Assert.DoesNotContain(reopened.Application.Drivers.Drivers, driver => driver.Id == "simulator");
        }
    }

    [Fact]
    public async Task ExistingConfigurationIsNotSilentlyPrunedWhenADriverIsMissing()
    {
        StageDrivers("uninstalled");
        var original = DemoConfiguration.Create();
        original.Devices[0].Name = "用户保存的设备";
        await using (var store = new SqliteConfigurationStore(Database)) await store.SaveAsync(original);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Start());
        Assert.Contains("simulator", error.Message);
        await using var saved = new SqliteConfigurationStore(Database);
        Assert.Equal(original.Devices.Select(device => device.Id), (await saved.LoadAsync()).Devices.Select(device => device.Id));
        Assert.Null(await saved.LoadPreviousAsync());
        using var lease = new FileStream(Path.Combine(_root, "gateway.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private Task<GatewayHost> Start() => GatewayHost.CreateCoreAsync(Data, Drivers, CancellationToken.None, false, _root, Drivers);

    private void StageDrivers(string state)
    {
        Directory.CreateDirectory(Drivers);
        File.Copy(typeof(ModbusTcpDriverFactory).Assembly.Location, Path.Combine(Drivers, "EdgeGateway.Driver.Modbus.dll"));
        if (state != "missing")
            File.Copy(typeof(SimulatorDriverFactory).Assembly.Location, Path.Combine(Drivers, "EdgeGateway.Driver.Simulator.dll"));
        if (state != "uninstalled") return;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("SIMULATOR")));
        var directory = Path.Combine(Drivers, "installed", key);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "driver-plugin.json"), JsonSerializer.Serialize(new
        {
            AssemblyFile = "", Sha256 = "", DriverId = "simulator", DriverName = "模拟数据", Version = "1.0.0.0",
            Active = false, DriverIds = new[] { "simulator" }, AssemblyName = "EdgeGateway.Driver.Simulator", Bundled = true
        }));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { /* Windows may retain mapped driver images until test process exit. */ }
        catch (UnauthorizedAccessException) { }
    }
}
