using System.Text.Json;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edge-config-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "gateway.config.db");

    [Fact]
    public async Task FreshDatabaseDoesNotClaimDefaultsAreSaved()
    {
        await using var store = new SqliteConfigurationStore(DatabasePath, DemoConfiguration.Create);
        var configuration = await store.LoadAsync();
        Assert.False(store.HasSavedConfiguration);
        Assert.Equal(3, configuration.SchemaVersion);
        Assert.Contains(configuration.Devices, device => device.DriverId == "simulator");
        await store.SaveAsync(configuration);
        Assert.True(store.HasSavedConfiguration);
    }

    [Fact]
    public async Task ConfigurationAndPreviousVersionRoundTripWithNewRecordingSettings()
    {
        var configuration = DemoConfiguration.Create();
        configuration.Logging.MaxFileSizeMb = 17;
        configuration.Databases.Add(new DatabaseConnectionConfiguration { Name = "生产数据", Provider = ExternalDatabaseProvider.PostgreSql });
        await using (var store = new SqliteConfigurationStore(DatabasePath))
        {
            await store.SaveAsync(configuration);
            configuration.Name = "第二版配置";
            configuration.Logging.MinimumLevel = GatewayLogLevel.Warning;
            await store.SaveAsync(configuration);
        }
        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        var current = await reopened.LoadAsync();
        Assert.True(reopened.HasSavedConfiguration);
        Assert.Equal("第二版配置", current.Name);
        Assert.Equal(GatewayLogLevel.Warning, current.Logging.MinimumLevel);
        Assert.Equal(17, current.Logging.MaxFileSizeMb);
        Assert.Equal(ExternalDatabaseProvider.PostgreSql, Assert.Single(current.Databases).Provider);
        var previous = await reopened.LoadPreviousAsync();
        Assert.NotNull(previous);
        Assert.Equal("边缘采集网关", previous.Name);
        Assert.Equal(GatewayLogLevel.Information, previous.Logging.MinimumLevel);
        Assert.Equal(current.Devices[0].Id, previous.Devices[0].Id);
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        check.Open();
        using var command = check.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read()) tables.Add(reader.GetString(0));
        Assert.Equal(["configuration_versions"], tables);
    }

    [Fact]
    public async Task SerializationFailureAndCancellationPreserveBothVersions()
    {
        await using var store = new SqliteConfigurationStore(DatabasePath);
        var config = DemoConfiguration.Create();
        await store.SaveAsync(config);
        config.Name = "已保存";
        await store.SaveAsync(config);
        config.Name = "禁止提交";
        config.Devices[0].Tags[0].Scale = double.NaN;
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(config));
        config.Devices[0].Tags[0].Scale = 1;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(config, new CancellationToken(true)));
        Assert.Equal("已保存", (await store.LoadAsync()).Name);
        Assert.Equal("边缘采集网关", (await store.LoadPreviousAsync())!.Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task UnsupportedJsonSchemasAreRejectedWithoutMigration(int version)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "import.json");
        var original = "{\"schemaVersion\":" + version + ",\"name\":\"原文件\"}";
        await File.WriteAllTextAsync(path, original);
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonConfigurationFiles().ReadAsync(path));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task JsonExchangeUsesCurrentSchemaWithoutBecomingRuntimeStorage()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "exchange.json");
        var exchange = new JsonConfigurationFiles();
        var config = DemoConfiguration.Create();
        await exchange.ExportAsync(path, config);
        var loaded = await exchange.ReadAsync(path);
        Assert.Equal(config.Devices[0].Id, loaded.Devices[0].Id);
        Assert.Equal(3, loaded.SchemaVersion);
        Assert.False(File.Exists(DatabasePath));
        await File.WriteAllTextAsync(path, "{ malformed");
        await Assert.ThrowsAsync<InvalidDataException>(() => exchange.ReadAsync(path));
        Assert.Equal("{ malformed", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CorruptSavedPayloadNeverFallsBackToDefaults()
    {
        await using (var store = new SqliteConfigurationStore(DatabasePath)) await store.SaveAsync(DemoConfiguration.Create());
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE configuration_versions SET payload='{broken' WHERE slot='current'";
            command.ExecuteNonQuery();
        }
        var factoryCalled = false;
        await using var reopened = new SqliteConfigurationStore(DatabasePath, () => { factoryCalled = true; return DemoConfiguration.Create(); });
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadAsync());
        Assert.False(factoryCalled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
