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

    [Fact]
    public async Task ExistingSchemaTwoFileKeepsAcquisitionAndSecurityDefaultsWhenNewCapabilitiesAreAbsent()
    {
        Directory.CreateDirectory(_directory);
        const string original = """
            {"schemaVersion":2,"gatewayId":"existing-gateway","devices":[
              {"id":"07bc5395-2c6f-4945-b815-57e72bf6ae21","name":"旧设备","driverId":"simulator","tags":[
                {"id":"d38ace68-4e8c-4786-bdc4-b1ba3959cdb2","name":"原点位","address":"constant:42","dataType":"Float64"}
              ]}
            ]}
            """;
        await File.WriteAllTextAsync(FilePath, original);
        var store = new JsonConfigurationStore(FilePath);
        var configuration = await store.LoadAsync();
        var device = Assert.Single(configuration.Devices);
        var tag = Assert.Single(device.Tags);
        Assert.Equal(2, configuration.SchemaVersion);
        Assert.Empty(configuration.Channels);
        Assert.Null(device.ChannelId);
        Assert.Equal(DeviceScanMode.DeviceRate, device.ScanMode);
        Assert.False(device.AutoDemotionEnabled);
        Assert.Equal("", tag.Expression);
        Assert.Equal(0, tag.ScanIntervalMs);
        Assert.Equal(0d, tag.Deadband);
        Assert.True(tag.UploadEnabled);
        Assert.Equal(TelemetryReportMode.Periodic, configuration.ReportMode);
        Assert.Equal(MqttPlatform.Generic, configuration.Mqtt.Platform);
        Assert.False(configuration.Mqtt.RpcEnabled);
        Assert.False(configuration.Mqtt.AllowRemoteWrite);
        Assert.False(configuration.OpcUa.Enabled);
        Assert.False(configuration.OpcUa.AllowRemoteWrite);
        Assert.False(configuration.OpcUa.EnableUserAuthentication);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
        await store.SaveAsync(configuration);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath + ".bak"));
        var reloaded = await store.LoadAsync();
        Assert.Equal(device.Id, reloaded.Devices[0].Id);
        Assert.Equal(tag.Id, reloaded.Devices[0].Tags[0].Id);
        Assert.False(reloaded.OpcUa.Enabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
