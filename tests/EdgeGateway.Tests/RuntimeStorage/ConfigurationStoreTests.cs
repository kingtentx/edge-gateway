using System.Text.Json;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edge-config-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "gateway.json");

    [Fact]
    public async Task MissingFileUsesFactoryButDoesNotCreateFileUntilSaved()
    {
        var store = new JsonConfigurationStore(FilePath, DemoConfiguration.Create);
        var config = await store.LoadAsync();
        Assert.Contains(config.Devices, d => d.DriverId == "simulator" && d.Enabled);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task AtomicSaveKeepsPreviousVersionAndReadsEnums()
    {
        var store = new JsonConfigurationStore(FilePath);
        var config = DemoConfiguration.Create();
        await store.SaveAsync(config);
        config.Name = "第二版配置";
        await store.SaveAsync(config);
        var current = await store.LoadAsync();
        var backup = await new JsonConfigurationStore(FilePath + ".bak").LoadAsync();
        Assert.Equal("第二版配置", current.Name);
        Assert.Equal("边缘采集网关", backup.Name);
        Assert.Equal(TagDataType.Float64, current.Devices[0].Tags[0].DataType);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task MalformedFileNeverFallsBackOrOverwrites()
    {
        Directory.CreateDirectory(_directory);
        const string original = "{invalid JSON content";
        await File.WriteAllTextAsync(FilePath, original);
        var factoryCalled = false;
        var store = new JsonConfigurationStore(FilePath, () => { factoryCalled = true; return new GatewayConfiguration(); });
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.False(factoryCalled);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task SerializationFailureLeavesExistingConfigurationIntact()
    {
        var store = new JsonConfigurationStore(FilePath);
        var config = DemoConfiguration.Create();
        await store.SaveAsync(config);
        var original = await File.ReadAllTextAsync(FilePath);
        config.Devices[0].Tags[0].Scale = double.NaN;
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(config));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task CancellationBeforeReplacePreservesExistingConfiguration()
    {
        var store = new JsonConfigurationStore(FilePath);
        var config = DemoConfiguration.Create();
        await store.SaveAsync(config);
        var original = await File.ReadAllTextAsync(FilePath);
        config.Name = "不应写入";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(config, new CancellationToken(true)));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task VersionOneUpgradesOnlyInMemoryUntilExplicitSaveAndRetainsOriginalBackup()
    {
        Directory.CreateDirectory(_directory);
        var originalConfiguration = DemoConfiguration.Create();
        originalConfiguration.SchemaVersion = 1;
        var original = JsonSerializer.Serialize(originalConfiguration, JsonConfigurationStore.SerializerOptions);
        await File.WriteAllTextAsync(FilePath, original);
        var store = new JsonConfigurationStore(FilePath);
        var upgraded = await store.LoadAsync();
        Assert.Equal(2, upgraded.SchemaVersion);
        Assert.Equal(originalConfiguration.Devices[0].Id, upgraded.Devices[0].Id);
        Assert.Equal(originalConfiguration.Devices[0].Tags[0].Id, upgraded.Devices[0].Tags[0].Id);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
        Assert.False(File.Exists(FilePath + ".bak"));
        await store.SaveAsync(upgraded);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath + ".bak"));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath));
        Assert.Equal(2, saved.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task UnknownSchemaVersionFailsWithoutChangingOriginal(int version)
    {
        Directory.CreateDirectory(_directory);
        var original = "{\"schemaVersion\":" + version + "}";
        await File.WriteAllTextAsync(FilePath, original);
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonConfigurationStore(FilePath).LoadAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
