using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class RelationalConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edge-relational-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "gateway.config.db");

    [Fact]
    public async Task AllFieldsAndOrderedRelationsSurviveRestartAndSnapshotRotation()
    {
        var configuration = CompleteConfiguration();
        var expected = JsonSerializer.SerializeToNode(configuration, GatewayJson.Options);
        await using (var store = new SqliteConfigurationStore(DatabasePath))
        {
            await store.SaveAsync(configuration);
            await store.SaveAsync(configuration);
            configuration.Devices.Reverse();
            configuration.Devices[0].Tags.Reverse();
            configuration.DatabaseWriteGroups[0].TagIds.Reverse();
            configuration.DatabaseWriteGroups[0].Columns.Reverse();
            configuration.Name = "第三份配置";
            await store.SaveAsync(configuration);
        }
        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(await reopened.LoadPreviousAsync(), GatewayJson.Options)));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(configuration, GatewayJson.Options),
            JsonSerializer.SerializeToNode(await reopened.LoadAsync(), GatewayJson.Options)));
        using var connection = Open();
        Assert.Equal(5L, Scalar(connection, "PRAGMA user_version"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM configuration_snapshots"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal("integer", Scalar(connection, "SELECT typeof(poll_interval_ms) FROM devices LIMIT 1"));
        Assert.Equal("real", Scalar(connection, "SELECT typeof(scale) FROM tags LIMIT 1"));
        Assert.Equal("Int64", Scalar(connection, "SELECT field_type FROM database_column_mappings WHERE source='Constant' LIMIT 1"));
        Assert.Equal("192", Scalar(connection, "SELECT constant_value FROM database_column_mappings WHERE source='Constant' LIMIT 1"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('configuration_versions','driver_plugins','driver_catalog')"));
    }

    [Theory]
    [InlineData("duplicate-device")]
    [InlineData("missing-channel")]
    [InlineData("missing-tag")]
    [InlineData("missing-connection")]
    public async Task FailedMultiTableSaveRollsBackBothSnapshots(string failure)
    {
        var configuration = CompleteConfiguration();
        await using var store = new SqliteConfigurationStore(DatabasePath);
        await store.SaveAsync(configuration);
        configuration.Name = "当前配置";
        await store.SaveAsync(configuration);
        var current = JsonSerializer.Serialize(await store.LoadAsync(), GatewayJson.Options);
        var previous = JsonSerializer.Serialize(await store.LoadPreviousAsync(), GatewayJson.Options);
        switch (failure)
        {
            case "duplicate-device": configuration.Devices.Add(configuration.Devices[0]); break;
            case "missing-channel": configuration.Devices[0].ChannelId = Guid.NewGuid(); break;
            case "missing-tag": configuration.DatabaseWriteGroups[0].TagIds.Add(Guid.NewGuid()); break;
            case "missing-connection": configuration.Databases.Clear(); break;
        }
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync(configuration));
        Assert.Equal(current, JsonSerializer.Serialize(await store.LoadAsync(), GatewayJson.Options));
        Assert.Equal(previous, JsonSerializer.Serialize(await store.LoadPreviousAsync(), GatewayJson.Options));
        // The same shared connection remains usable after rollback.
        await store.SaveAsync(await store.LoadAsync());
        Assert.Equal(current, JsonSerializer.Serialize(await store.LoadPreviousAsync(), GatewayJson.Options));
    }

    [Fact]
    public async Task ReplacingWithEmptyCollectionsRemovesCurrentChildrenOnly()
    {
        await using var store = new SqliteConfigurationStore(DatabasePath);
        await store.SaveAsync(CompleteConfiguration());
        await store.SaveAsync(new GatewayConfiguration());
        Assert.Empty((await store.LoadAsync()).Devices);
        Assert.Equal(2, (await store.LoadPreviousAsync())!.Devices.Count);
        using var connection = Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM tags WHERE slot='current'"));
        await store.SaveAsync(new GatewayConfiguration());
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM tags"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM device_properties"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM database_column_mappings"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(99)]
    public async Task OldOrUnversionedNonemptyDatabaseIsRejectedWithoutRemovingTables(int version)
    {
        using (var connection = Open())
        {
            Execute(connection, $"PRAGMA user_version={version}; CREATE TABLE configuration_versions(payload TEXT); INSERT INTO configuration_versions VALUES('original');");
        }
        var original = await File.ReadAllBytesAsync(DatabasePath);
        await using var store = new SqliteConfigurationStore(DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new GatewayConfiguration()));
        Assert.Equal(original, await File.ReadAllBytesAsync(DatabasePath));
    }

    [Fact]
    public async Task CurrentLayoutWithMissingTableIsNotSilentlyRecreated()
    {
        await using (var store = new SqliteConfigurationStore(DatabasePath)) await store.SaveAsync(new GatewayConfiguration());
        using (var connection = Open()) Execute(connection, "DROP TABLE rpc_settings");
        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadAsync());
        using var check = Open();
        Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM sqlite_master WHERE name='rpc_settings'"));
    }

    [Fact]
    public async Task OrphanedRowsAreRejectedInsteadOfSilentlyOmittedFromConfiguration()
    {
        await using (var store = new SqliteConfigurationStore(DatabasePath))
        {
            await store.SaveAsync(CompleteConfiguration());
            await store.SaveAsync(CompleteConfiguration());
        }
        using (var connection = Open())
            Execute(connection, "PRAGMA foreign_keys=OFF; UPDATE tags SET device_id='missing-' || device_id WHERE slot='current'");
        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadAsync());
        Assert.Equal(2, (await reopened.LoadPreviousAsync())!.Devices.Count);
    }

    [Fact]
    public async Task CancellationDuringMultiTableWriteRestoresBothSnapshots()
    {
        await using (var store = new SqliteConfigurationStore(DatabasePath))
        {
            await store.SaveAsync(CompleteConfiguration());
            await store.SaveAsync(CompleteConfiguration());
        }
        using (var connection = Open())
        {
            Execute(connection, "PRAGMA foreign_keys=ON");
            using var cancellation = new CancellationTokenSource();
            connection.CreateFunction("cancel_save", () => { cancellation.Cancel(); return 1; });
            Execute(connection, "CREATE TEMP TRIGGER cancel_write AFTER INSERT ON tags BEGIN SELECT cancel_save(); END;");
            Assert.ThrowsAny<OperationCanceledException>(() => RelationalConfiguration.Save(connection,
                RelationalConfiguration.Capture(new GatewayConfiguration { Devices = [new DeviceConfiguration { Tags = [new TagConfiguration()] }] }), cancellation.Token));
        }
        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        Assert.Equal(2, (await reopened.LoadAsync()).Devices.Count);
        Assert.Equal(2, (await reopened.LoadPreviousAsync())!.Devices.Count);
    }

    [Fact]
    public async Task DriverMetadataHasOneRowAndRefreshDoesNotRemovePluginRegistration()
    {
        var plugin = new DriverPluginInfo("custom", "Custom", "2.0", "Custom.Driver", new string('A', 64), "已加载");
        var inventory = new DriverInventoryRecord(plugin.Id, plugin.Name, "description", true, false,
            plugin.AssemblyName, plugin.Version, plugin.Sha256, "uploaded");
        await using (var store = new SqliteConfigurationStore(DatabasePath))
        {
            await store.SaveDriverPluginAsync(plugin);
            await store.ReplaceDriverInventoryAsync([inventory]);
            Assert.Equal(plugin, Assert.Single(await store.GetDriverPluginsAsync()));
            Assert.Equal(inventory, Assert.Single(await store.GetDriverInventoryAsync()));
            using var connection = Open();
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM driver_registry"));
            await store.ReplaceDriverInventoryAsync([]);
        }
        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        Assert.Equal(plugin, Assert.Single(await reopened.GetDriverPluginsAsync()));
        Assert.Empty(await reopened.GetDriverInventoryAsync());
        await reopened.ReplaceDriverPluginsAsync(["CUSTOM"], []);
        Assert.Empty(await reopened.GetDriverPluginsAsync());
        using var check = Open();
        Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM driver_registry"));
    }

    private static GatewayConfiguration CompleteConfiguration()
    {
        var config = FillScalars(new GatewayConfiguration());
        config.SchemaVersion = 3;
        config.Mqtt = FillScalars(new MqttConfiguration());
        config.Logging = FillScalars(new GatewayLoggingConfiguration());
        config.Channels = [FillScalars(new ChannelConfiguration()), FillScalars(new ChannelConfiguration())];
        config.Devices = [FillScalars(new DeviceConfiguration()), FillScalars(new DeviceConfiguration())];
        for (var i = 0; i < config.Devices.Count; i++)
        {
            var device = config.Devices[i];
            device.ChannelId = i == 0 ? config.Channels[0].Id : null;
            device.Properties = new() { ["Authorization"] = " test-only ' ; ", ["未知参数"] = "值" };
            device.Tags = [FillScalars(new TagConfiguration()), FillScalars(new TagConfiguration())];
            device.Tags[0].LowLimit = null;
        }
        config.Databases = [FillScalars(new DatabaseConnectionConfiguration()), FillScalars(new DatabaseConnectionConfiguration())];
        var group = FillScalars(new DatabaseWriteGroupConfiguration());
        group.ConnectionId = config.Databases[0].Id;
        group.Mode = DatabaseStorageMode.PointMapping;
        group.TagIds = [config.Devices[1].Tags[1].Id, config.Devices[0].Tags[0].Id];
        var mapped = FillScalars(new DatabaseColumnMapping());
        mapped.TagId = group.TagIds[0];
        mapped.Source = DatabaseValueSource.RawValue;
        group.Columns = [mapped, new() { Column = "_QUALITY", Source = DatabaseValueSource.Constant, FieldType = DatabaseFieldType.Int64, ConstantValue = "192" }];
        config.DatabaseWriteGroups = [group];
        return config;
    }

    // Exercise every scalar with a non-default value so omitted storage columns cannot hide behind model defaults.
    private static T FillScalars<T>(T target)
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = type == typeof(string) ? "值-'\"-" + property.Name
                : type == typeof(Guid) ? Guid.NewGuid()
                : type == typeof(bool) ? !(bool)property.GetValue(target)!
                : type == typeof(int) ? 12345
                : type == typeof(double) ? 17.125
                : type.IsEnum ? Enum.GetValues(type).GetValue(Enum.GetValues(type).Length - 1) : null;
            if (value is not null) property.SetValue(target, value);
        }
        return target;
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
