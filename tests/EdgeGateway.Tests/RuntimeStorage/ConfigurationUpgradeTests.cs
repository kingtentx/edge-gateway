using System.Text.Json;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class ConfigurationUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edge-upgrade-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "gateway.config.db");
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private async Task<GatewayConfiguration> CreateVersionSixAsync()
    {
        var configuration = DemoConfiguration.Create(); configuration.AutoStart = false; configuration.Mqtt.Password = "fake-test-password";
        await using (var store = new SqliteConfigurationStore(DatabasePath))
        {
            await store.SaveAsync(configuration);
            configuration.Name = "当前配置"; await store.SaveAsync(configuration);
            await store.SaveDriverPluginAsync(new("fixture", "测试插件", "1.0.0", "Fixture.Driver", new string('A', 64), "已加载"));
        }
        using var connection = Open();
        Execute(connection, "DROP TABLE database_point_rules; DROP TABLE device_parameter_metadata; PRAGMA user_version=6;");
        return configuration;
    }
    [Fact]
    public async Task UpgradePreservesCurrentPreviousIdsCredentialsPluginsAndWalBackup()
    {
        var expected = await CreateVersionSixAsync();
        using var wal = Open();
        Execute(wal, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; UPDATE gateway_settings SET name='WAL配置' WHERE slot='current';");
        expected.Name = "WAL配置";
        await using (var store = new SqliteConfigurationStore(DatabasePath))
        {
            var actual = await store.LoadAsync();
            Assert.Equal(JsonSerializer.Serialize(expected, GatewayJson.Options), JsonSerializer.Serialize(actual, GatewayJson.Options));
            Assert.Equal("边缘采集网关", (await store.LoadPreviousAsync())!.Name);
            Assert.Equal("fixture", Assert.Single(await store.GetDriverPluginsAsync()).Id);
            Assert.NotNull(store.UpgradeBackupPath);
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.UpgradeBackupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            backup.Open(); Assert.Equal(6L, Scalar(backup, "PRAGMA user_version"));
            Assert.Equal("WAL配置", Scalar(backup, "SELECT name FROM gateway_settings WHERE slot='current'"));
            actual.Devices[0].ParameterMetadata["Endpoint"] = new() { Name = "服务地址", Description = "升级后编辑" };
            await store.SaveAsync(actual);
        }
        await using var reopened = new SqliteConfigurationStore(DatabasePath);
        Assert.Equal("升级后编辑", (await reopened.LoadAsync()).Devices[0].ParameterMetadata["Endpoint"].Description);
        Assert.Null(reopened.UpgradeBackupPath);
        Assert.Single(Directory.GetFiles(_root, "*.v6-backup-*.db"));
        Assert.Equal(8L, Scalar(wal, "PRAGMA user_version"));
    }
    [Fact]
    public async Task FailedUpgradeRollsBackVersionAndRetainsBackupAndConfiguration()
    {
        await CreateVersionSixAsync();
        using (var connection = Open()) Execute(connection, "CREATE TABLE device_parameter_metadata(conflict TEXT);");
        await using var store = new SqliteConfigurationStore(DatabasePath);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Contains("未提交结构变更", error.Message);
        Assert.Single(Directory.GetFiles(_root, "*.v6-backup-*.db"));
        using var check = Open(); Assert.Equal(6L, Scalar(check, "PRAGMA user_version"));
        Assert.Equal("当前配置", Scalar(check, "SELECT name FROM gateway_settings WHERE slot='current'"));
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task VersionSevenUpgradePreservesWalAndRuleRoundTrip()
    {
        var original = DemoConfiguration.Create();
        var connection = new DatabaseConnectionConfiguration(); original.Databases.Add(connection);
        var group = new DatabaseWriteGroupConfiguration { ConnectionId = connection.Id };
        original.DatabaseWriteGroups.Add(group);
        await using (var store = new SqliteConfigurationStore(DatabasePath)) await store.SaveAsync(original);
        using var wal = Open();
        Execute(wal, "DROP TABLE database_point_rules; PRAGMA user_version=7; PRAGMA wal_autocheckpoint=0; UPDATE gateway_settings SET name='WAL7' WHERE slot='current';");
        await using var upgraded = new SqliteConfigurationStore(DatabasePath);
        var loaded = await upgraded.LoadAsync();
        Assert.Equal("WAL7", loaded.Name);
        Assert.Equal(original.Devices.Select(device => device.Id), loaded.Devices.Select(device => device.Id));
        Assert.Equal(DatabasePointSelectionMode.Points, loaded.DatabaseWriteGroups[0].Selection.Mode);
        Assert.Equal(8L, Scalar(wal, "PRAGMA user_version"));
        using var backup = new SqliteConnection($"Data Source={upgraded.UpgradeBackupPath};Mode=ReadOnly;Pooling=False");
        backup.Open(); Assert.Equal(7L, Scalar(backup, "PRAGMA user_version"));
        Assert.Equal("WAL7", Scalar(backup, "SELECT name FROM gateway_settings WHERE slot='current'"));
        loaded.DatabaseWriteGroups[0].Selection = new() { Mode = DatabasePointSelectionMode.Device, DeviceId = loaded.Devices[0].Id };
        await upgraded.SaveAsync(loaded);
        Assert.Equal(loaded.Devices[0].Id, (await upgraded.LoadAsync()).DatabaseWriteGroups[0].Selection.DeviceId);
    }

    [Fact]
    public async Task VersionSevenConflictRollsBackAndRetainsBackup()
    {
        await using (var store = new SqliteConfigurationStore(DatabasePath)) await store.SaveAsync(DemoConfiguration.Create());
        using var connection = Open();
        Execute(connection, "PRAGMA user_version=7;"); // Existing table conflicts with the migration.
        await using var upgraded = new SqliteConfigurationStore(DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => upgraded.LoadAsync());
        Assert.Equal(7L, Scalar(connection, "PRAGMA user_version"));
        Assert.Single(Directory.GetFiles(_root, "*.v7-backup-*.db"));
    }
}
