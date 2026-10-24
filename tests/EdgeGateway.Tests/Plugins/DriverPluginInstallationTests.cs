using EdgeGateway.Abstractions;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.Plugins;

public sealed class DriverPluginInstallationTests
{
    [Fact]
    public async Task UploadedDllRegistersImmediatelyAndIsDiscoveredAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-driver-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var log = new GatewayLog();
        try
        {
            var registry = new DriverRegistry(Array.Empty<IDriverFactory>(), log);
            registry.LoadPlugins(root);
            var source = Path.Combine(AppContext.BaseDirectory, "EdgeGateway.Driver.TestPlugin.dll");
            Assert.True(File.Exists(source), "The fixture plugin DLL must be copied to the test output.");
            var message = registry.InstallPlugin(source);
            Assert.Contains("立即注册", message);
            Assert.Contains(registry.Drivers, driver => driver.Id == "test-dynamic-plugin");
            Assert.Single(registry.GetInstalledPlugins());

            var restarted = new DriverRegistry(Array.Empty<IDriverFactory>(), log);
            restarted.LoadPlugins(root);
            Assert.Contains(restarted.Drivers, driver => driver.Id == "test-dynamic-plugin");
            Assert.Equal("Dynamic plugin fixture", restarted.GetFactory("test-dynamic-plugin").Descriptor.Name);
        }
        finally
        {
            // The plugin context is collectible; the test process still owns the registry until scope exit.
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task InvalidDllIsRejectedWithoutChangingRegisteredDrivers()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-driver-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var invalid = Path.Combine(root, "not-a-driver.dll");
        await File.WriteAllTextAsync(invalid, "not a PE assembly");
        await using var log = new GatewayLog();
        var registry = new DriverRegistry(Array.Empty<IDriverFactory>(), log);
        registry.LoadPlugins(Path.Combine(root, "plugins"));
        try
        {
            Assert.ThrowsAny<Exception>(() => registry.InstallPlugin(invalid));
            Assert.Empty(registry.Drivers);
            Assert.Empty(registry.GetInstalledPlugins());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task InstalledPluginHashMismatchIsRejectedOnRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-driver-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var log = new GatewayLog();
        try
        {
            var registry = new DriverRegistry(Array.Empty<IDriverFactory>(), log);
            registry.LoadPlugins(root);
            registry.InstallPlugin(Path.Combine(AppContext.BaseDirectory, "EdgeGateway.Driver.TestPlugin.dll"));
            var dll = Directory.GetFiles(Path.Combine(root, "installed"), "EdgeGateway.Driver.TestPlugin.dll", SearchOption.AllDirectories).Single();
            var bytes = await File.ReadAllBytesAsync(dll);
            bytes[^1] ^= 0x01;
            await File.WriteAllBytesAsync(dll, bytes);

            var restarted = new DriverRegistry(Array.Empty<IDriverFactory>(), log);
            restarted.LoadPlugins(root);
            Assert.DoesNotContain(restarted.Drivers, driver => driver.Id == "test-dynamic-plugin");
            Assert.Contains(log.GetEntries(), entry => entry.Level == GatewayLogLevel.Error && entry.Message.Contains("SHA-256"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
