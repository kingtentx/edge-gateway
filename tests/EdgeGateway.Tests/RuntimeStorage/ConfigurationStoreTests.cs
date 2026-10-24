using System.Text.Json;
using EdgeGateway.Abstractions;
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
    public async Task LiteralCredentialsSurviveSqliteRestartAndConfigurationExchange()
    {
        var configuration = DemoConfiguration.Create();
        configuration.Mqtt.Password = "  test-mqtt-password;\"!  ";
        configuration.Mqtt.AccessToken = "  test-platform-token/+=  ";
        configuration.Databases.Add(new DatabaseConnectionConfiguration { Password = "  test-db-password;\"!  " });
        configuration.Devices[0].Properties["AuthorizationCode"] = "test-license-value";
        await using (var store = new SqliteConfigurationStore(DatabasePath)) await store.SaveAsync(configuration);

        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        var saved = await reopened.LoadAsync();
        var exchange = Path.Combine(_directory, "credentials.json");
        var files = new JsonConfigurationFiles();
        await files.ExportAsync(exchange, saved);
        var imported = await files.ReadAsync(exchange);
        Assert.Equal(configuration.Mqtt.Password, imported.Mqtt.Password);
        Assert.Equal(configuration.Mqtt.AccessToken, imported.Mqtt.AccessToken);
        Assert.Equal(configuration.Databases[0].Password, Assert.Single(imported.Databases).Password);
        Assert.Equal("test-license-value", imported.Devices[0].Properties["AuthorizationCode"]);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(exchange));
        var mqtt = document.RootElement.GetProperty("mqtt");
        Assert.Equal(configuration.Mqtt.Password, mqtt.GetProperty("password").GetString());
        Assert.DoesNotContain(mqtt.EnumerateObject(), field => field.Name.EndsWith("EnvironmentVariable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(document.RootElement.GetProperty("databases")[0].EnumerateObject(),
            field => field.Name.EndsWith("EnvironmentVariable", StringComparison.OrdinalIgnoreCase));
    }

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
        Assert.Equal(["configuration_versions", "driver_catalog", "driver_plugins"], tables);
    }


    [Fact]
    public async Task DriverPluginInventoryPersistsInTheSameSqliteDatabase()
    {
        await using (var store = new SqliteConfigurationStore(DatabasePath, DemoConfiguration.Create))
        {
            _ = await store.LoadAsync();
            await store.SaveDriverPluginAsync(new DriverPluginInfo("custom-plc", "自定义 PLC", "1.2.3.0",
                "Custom.Plc.Driver", new string('A', 64), "已加载"));
        }

        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        var plugin = Assert.Single(await reopened.GetDriverPluginsAsync());
        Assert.Equal("custom-plc", plugin.Id);
        Assert.Equal("自定义 PLC", plugin.Name);
        Assert.Equal("1.2.3.0", plugin.Version);
        Assert.Equal("Custom.Plc.Driver", plugin.AssemblyName);
        Assert.Equal(new string('A', 64), plugin.Sha256);
        Assert.Equal("已加载", plugin.Status);
    }


    [Fact]
    public async Task DriverCatalogInventoryIsReplacedAtomicallyInSqlite()
    {
        await using var store = new SqliteConfigurationStore(DatabasePath, DemoConfiguration.Create);
        _ = await store.LoadAsync();
        await store.ReplaceDriverInventoryAsync([
            new("modbus-tcp", "Modbus TCP", "TCP driver", true, false, "EdgeGateway.Driver.Modbus", "1.0.0.0", new string('A', 64), "bundled"),
            new("custom-plc", "Custom PLC", "Uploaded protocol", false, false, "Custom.Plc.Driver", "2.0.0.0", new string('B', 64), "uploaded")
        ]);
        var first = await store.GetDriverInventoryAsync();
        Assert.Equal(2, first.Count);
        Assert.Contains(first, item => item.Id == "custom-plc" && item.Source == "uploaded");
        await store.ReplaceDriverInventoryAsync([
            new("modbus-tcp", "Modbus TCP", "TCP driver", true, false, "EdgeGateway.Driver.Modbus", "1.0.0.0", new string('A', 64), "bundled")
        ]);
        var current = Assert.Single(await store.GetDriverInventoryAsync());
        Assert.Equal("modbus-tcp", current.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InventoryRefreshKeepsConfigurationAndPluginStorageUsable(bool initiallySaved)
    {
        var configuration = DemoConfiguration.Create();
        var originalName = configuration.Name;
        var driver = new DriverInventoryRecord("modbus-tcp", "Modbus TCP", "TCP driver", true, false,
            "EdgeGateway.Driver.Modbus", "1.0.0.0", new string('A', 64), "bundled");
        var plugin = new DriverPluginInfo("custom-plc", "自定义 PLC", "1.2.3.0",
            "Custom.Plc.Driver", new string('B', 64), "已加载");

        await using (var store = new SqliteConfigurationStore(DatabasePath))
        {
            if (initiallySaved) await store.SaveAsync(configuration);
            await store.ReplaceDriverInventoryAsync([driver]);
            Assert.Equal(initiallySaved, store.HasSavedConfiguration);

            configuration.Name = "驱动清单刷新后保存";
            await store.SaveAsync(configuration);
            Assert.True(store.HasSavedConfiguration);
            Assert.Equal(configuration.Name, (await store.LoadAsync()).Name);
            var previous = await store.LoadPreviousAsync();
            if (initiallySaved) Assert.Equal(originalName, previous!.Name);
            else Assert.Null(previous);

            await store.SaveDriverPluginAsync(plugin);
            Assert.Equal(plugin, Assert.Single(await store.GetDriverPluginsAsync()));
            Assert.Equal(driver, Assert.Single(await store.GetDriverInventoryAsync()));

            await store.ReplaceDriverInventoryAsync([]);
            configuration.Name = "再次刷新后保存";
            await store.SaveAsync(configuration);
            Assert.Empty(await store.GetDriverInventoryAsync());
        }

        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        Assert.Equal("再次刷新后保存", (await reopened.LoadAsync()).Name);
        Assert.Equal("驱动清单刷新后保存", (await reopened.LoadPreviousAsync())!.Name);
        Assert.Equal(plugin, Assert.Single(await reopened.GetDriverPluginsAsync()));
        Assert.Empty(await reopened.GetDriverInventoryAsync());
    }

    [Fact]
    public async Task FailedInventoryRefreshRollsBackAndAllowsSubsequentConfigurationSave()
    {
        await using var store = new SqliteConfigurationStore(DatabasePath);
        var configuration = DemoConfiguration.Create();
        await store.SaveAsync(configuration);
        var driver = new DriverInventoryRecord("modbus-tcp", "Modbus TCP", "TCP driver", true, false,
            "EdgeGateway.Driver.Modbus", "1.0.0.0", new string('A', 64), "bundled");
        await store.ReplaceDriverInventoryAsync([driver]);

        // The duplicate primary key fails after DELETE/INSERT inside the transaction.
        var duplicate = driver with { Id = "duplicate" };
        await Assert.ThrowsAsync<SqliteException>(() => store.ReplaceDriverInventoryAsync([duplicate, duplicate]));
        Assert.Equal(driver, Assert.Single(await store.GetDriverInventoryAsync()));
        Assert.Equal(configuration.Name, (await store.LoadAsync()).Name);
        Assert.Null(await store.LoadPreviousAsync());

        var originalName = configuration.Name;
        configuration.Name = "清单事务回滚后保存";
        await store.SaveAsync(configuration);
        Assert.Equal(configuration.Name, (await store.LoadAsync()).Name);
        Assert.Equal(originalName, (await store.LoadPreviousAsync())!.Name);
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
    public async Task MissingJsonSchemaIsRejectedWithoutChangingTheFileOrCreatingRuntimeStorage()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "missing-schema.json");
        const string original = "{\"name\":\"未声明版本的配置\",\"devices\":[]}";
        await File.WriteAllTextAsync(path, original);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new JsonConfigurationFiles().ReadAsync(path));

        Assert.Contains("缺少 schemaVersion", error.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(DatabasePath));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("previous")]
    public async Task MissingPersistedSchemaIsRejectedWithoutDefaultingOrChangingEitherVersion(string slot)
    {
        await using (var store = new SqliteConfigurationStore(DatabasePath))
        {
            await store.SaveAsync(new GatewayConfiguration { Name = "有效上一份配置" });
            await store.SaveAsync(new GatewayConfiguration { Name = "有效当前配置" });
        }
        ReplaceSavedPayload(slot, "{\"name\":\"未声明版本的配置\"}");
        var before = ReadSavedPayloads();
        var factoryCalled = false;
        await using var reopened = new SqliteConfigurationStore(DatabasePath,
            () => { factoryCalled = true; return DemoConfiguration.Create(); });

        var error = slot == "current"
            ? await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadAsync())
            : await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadPreviousAsync());

        Assert.Contains("缺少 schemaVersion", error.Message);
        Assert.False(factoryCalled);
        if (slot == "current") Assert.Equal("有效上一份配置", (await reopened.LoadPreviousAsync())!.Name);
        else Assert.Equal("有效当前配置", (await reopened.LoadAsync()).Name);
        var after = ReadSavedPayloads();
        Assert.Equal(before["current"], after["current"]);
        Assert.Equal(before["previous"], after["previous"]);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("SchemaVersion")]
    [InlineData("SCHEMAVERSION")]
    public async Task ExplicitSchemaUsesSharedCaseInsensitiveNamesForFilesAndStoredPayloads(string propertyName)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "explicit-schema.json");
        var payload = "{\"" + propertyName + "\":3,\"name\":\"明确的新版本配置\"}";
        await File.WriteAllTextAsync(path, payload);
        var imported = await new JsonConfigurationFiles().ReadAsync(path);
        Assert.Equal(3, imported.SchemaVersion);
        Assert.Equal("明确的新版本配置", imported.Name);

        await using (var store = new SqliteConfigurationStore(DatabasePath)) await store.SaveAsync(imported);
        ReplaceSavedPayload("current", payload);
        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        var restored = await reopened.LoadAsync();
        Assert.Equal(3, restored.SchemaVersion);
        Assert.Equal(imported.Name, restored.Name);
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

    private void ReplaceSavedPayload(string slot, string payload)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE configuration_versions SET payload=$payload WHERE slot=$slot";
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$slot", slot);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private Dictionary<string, string> ReadSavedPayloads()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT slot,payload FROM configuration_versions";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>();
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
