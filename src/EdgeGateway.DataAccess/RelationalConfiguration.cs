using EdgeGateway.Model;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.DataAccess;

/// <summary>Functional schema. JSON schema 3 is an exchange contract; SQLite layout version 5 is independent.</summary>
internal static class RelationalConfiguration
{
    internal const int StorageVersion = 5;
    private const string Slot = "slot TEXT NOT NULL";
    private const string SnapshotForeignKey = "FOREIGN KEY(slot) REFERENCES configuration_snapshots(slot) ON DELETE CASCADE ON UPDATE CASCADE";
    private const string Singleton = "PRIMARY KEY(slot), " + SnapshotForeignKey;
    private const string Ordered = "PRIMARY KEY(slot,id), UNIQUE(slot,position), " + SnapshotForeignKey;
    private const string DeviceForeignKey = "FOREIGN KEY(slot,device_id) REFERENCES devices(slot,id) ON DELETE CASCADE ON UPDATE CASCADE";
    private const string GroupForeignKey = "FOREIGN KEY(slot,group_id) REFERENCES database_write_groups(slot,id) ON DELETE CASCADE ON UPDATE CASCADE";

    private static readonly ConfigurationTable<GatewayConfiguration> Gateway = new("gateway_settings",
        Slot, Singleton,
        nameof(GatewayConfiguration.GatewayId), nameof(GatewayConfiguration.Name), nameof(GatewayConfiguration.AutoStart));

    private static readonly ConfigurationTable<GatewayConfiguration> Telemetry = new("telemetry_settings",
        Slot, Singleton,
        nameof(GatewayConfiguration.MaxOutboxMessages), nameof(GatewayConfiguration.PublishIntervalMs), nameof(GatewayConfiguration.ReportMode), nameof(GatewayConfiguration.HeartbeatIntervalMs));

    private static readonly ConfigurationTable<MqttConfiguration> Mqtt = new("mqtt_settings",
        Slot, Singleton,
        nameof(MqttConfiguration.Enabled), nameof(MqttConfiguration.Host), nameof(MqttConfiguration.Port), nameof(MqttConfiguration.UseTls), nameof(MqttConfiguration.ClientId), nameof(MqttConfiguration.Username), nameof(MqttConfiguration.Password), nameof(MqttConfiguration.Topic), nameof(MqttConfiguration.TimeoutMs), nameof(MqttConfiguration.Platform), nameof(MqttConfiguration.AccessToken), nameof(MqttConfiguration.PlatformDeviceType), nameof(MqttConfiguration.ThingsCloudTypeKey), nameof(MqttConfiguration.PlatformGatewayId));

    private static readonly ConfigurationTable<MqttConfiguration> Rpc = new("rpc_settings",
        Slot, Singleton,
        nameof(MqttConfiguration.RpcEnabled), nameof(MqttConfiguration.AllowRemoteWrite), nameof(MqttConfiguration.RpcRequestTopic), nameof(MqttConfiguration.RpcResponseTopic), nameof(MqttConfiguration.RpcTimeoutMs));

    private static readonly ConfigurationTable<GatewayLoggingConfiguration> Logging = new("logging_settings",
        Slot, Singleton,
        nameof(GatewayLoggingConfiguration.MinimumLevel), nameof(GatewayLoggingConfiguration.LogAcquisitionSamples), nameof(GatewayLoggingConfiguration.MaxFileSizeMb), nameof(GatewayLoggingConfiguration.RetainedFileCount), nameof(GatewayLoggingConfiguration.RetainedDays), nameof(GatewayLoggingConfiguration.SampleMaxFileSizeMb), nameof(GatewayLoggingConfiguration.SampleRetainedFileCount), nameof(GatewayLoggingConfiguration.SampleRetainedDays), nameof(GatewayLoggingConfiguration.MinimumFreeDiskSpaceMb), nameof(GatewayLoggingConfiguration.DiskResumeMarginMb));

    private static readonly ConfigurationTable<ChannelConfiguration> Channels = new("channels",
        Slot + ", position INTEGER NOT NULL CHECK(position>=0)", Ordered,
        nameof(ChannelConfiguration.Id), nameof(ChannelConfiguration.Name), nameof(ChannelConfiguration.InterRequestDelayMs), nameof(ChannelConfiguration.Description));

    private static readonly ConfigurationTable<DeviceConfiguration> Devices = new("devices",
        Slot + ", position INTEGER NOT NULL CHECK(position>=0)", Ordered + ", FOREIGN KEY(slot,channel_id) REFERENCES channels(slot,id)",
        nameof(DeviceConfiguration.Id), nameof(DeviceConfiguration.Name), nameof(DeviceConfiguration.Group), nameof(DeviceConfiguration.ChannelId), nameof(DeviceConfiguration.DriverId), nameof(DeviceConfiguration.Enabled), nameof(DeviceConfiguration.PollIntervalMs), nameof(DeviceConfiguration.TimeoutMs), nameof(DeviceConfiguration.ScanMode), nameof(DeviceConfiguration.AutoDemotionEnabled), nameof(DeviceConfiguration.DemotionFailureCount), nameof(DeviceConfiguration.DemotionIntervalMs));

    private static readonly ConfigurationTable<DeviceConfiguration> Network = new("device_network_settings",
        Slot + ", device_id TEXT NOT NULL", "PRIMARY KEY(slot,device_id), " + DeviceForeignKey,
        nameof(DeviceConfiguration.Host), nameof(DeviceConfiguration.Port), nameof(DeviceConfiguration.UnitId));

    private static readonly ConfigurationTable<DeviceConfiguration> Serial = new("device_serial_settings",
        Slot + ", device_id TEXT NOT NULL", "PRIMARY KEY(slot,device_id), " + DeviceForeignKey,
        nameof(DeviceConfiguration.SerialPortName), nameof(DeviceConfiguration.BaudRate), nameof(DeviceConfiguration.DataBits), nameof(DeviceConfiguration.Parity), nameof(DeviceConfiguration.StopBits));

    private static readonly ConfigurationTable<TagConfiguration> Tags = new("tags",
        Slot + ", device_id TEXT NOT NULL, position INTEGER NOT NULL CHECK(position>=0)", "PRIMARY KEY(slot,id), UNIQUE(slot,device_id,position), " + DeviceForeignKey,
        nameof(TagConfiguration.Id), nameof(TagConfiguration.Name), nameof(TagConfiguration.Address), nameof(TagConfiguration.DataType), nameof(TagConfiguration.Area), nameof(TagConfiguration.ByteOrder), nameof(TagConfiguration.ReadMethod), nameof(TagConfiguration.WriteMethod), nameof(TagConfiguration.Endian), nameof(TagConfiguration.Enabled), nameof(TagConfiguration.UploadEnabled), nameof(TagConfiguration.Writable), nameof(TagConfiguration.Scale), nameof(TagConfiguration.Offset), nameof(TagConfiguration.Expression), nameof(TagConfiguration.ScanIntervalMs), nameof(TagConfiguration.Deadband), nameof(TagConfiguration.Unit), nameof(TagConfiguration.LowLimit), nameof(TagConfiguration.HighLimit), nameof(TagConfiguration.Description));

    private static readonly ConfigurationTable<DatabaseConnectionConfiguration> Databases = new("database_connections",
        Slot + ", position INTEGER NOT NULL CHECK(position>=0)", Ordered,
        nameof(DatabaseConnectionConfiguration.Id), nameof(DatabaseConnectionConfiguration.Name), nameof(DatabaseConnectionConfiguration.Enabled), nameof(DatabaseConnectionConfiguration.Provider), nameof(DatabaseConnectionConfiguration.Host), nameof(DatabaseConnectionConfiguration.Port), nameof(DatabaseConnectionConfiguration.Database), nameof(DatabaseConnectionConfiguration.Username), nameof(DatabaseConnectionConfiguration.Password), nameof(DatabaseConnectionConfiguration.UseTls), nameof(DatabaseConnectionConfiguration.ConnectionTimeoutSeconds), nameof(DatabaseConnectionConfiguration.CommandTimeoutSeconds), nameof(DatabaseConnectionConfiguration.BatchSize), nameof(DatabaseConnectionConfiguration.FlushIntervalMs), nameof(DatabaseConnectionConfiguration.QueueCapacityRows), nameof(DatabaseConnectionConfiguration.QueueCapacityMb), nameof(DatabaseConnectionConfiguration.MaxRetryAttempts));

    private static readonly ConfigurationTable<DatabaseWriteGroupConfiguration> Groups = new("database_write_groups",
        Slot + ", position INTEGER NOT NULL CHECK(position>=0)", Ordered + ", FOREIGN KEY(slot,connection_id) REFERENCES database_connections(slot,id)",
        nameof(DatabaseWriteGroupConfiguration.Id), nameof(DatabaseWriteGroupConfiguration.Name), nameof(DatabaseWriteGroupConfiguration.Enabled), nameof(DatabaseWriteGroupConfiguration.ConnectionId), nameof(DatabaseWriteGroupConfiguration.Mode), nameof(DatabaseWriteGroupConfiguration.Schema), nameof(DatabaseWriteGroupConfiguration.Table), nameof(DatabaseWriteGroupConfiguration.WriteIntervalMs), nameof(DatabaseWriteGroupConfiguration.MaxSampleAgeMs), nameof(DatabaseWriteGroupConfiguration.RequireGoodQuality));

    private static readonly ConfigurationTable<DatabaseColumnMapping> Columns = new("database_column_mappings",
        Slot + ", group_id TEXT NOT NULL, position INTEGER NOT NULL CHECK(position>=0)", "PRIMARY KEY(slot,group_id,position), UNIQUE(slot,group_id,\"column\"), " + GroupForeignKey + ", FOREIGN KEY(slot,tag_id) REFERENCES tags(slot,id)",
        nameof(DatabaseColumnMapping.Column), nameof(DatabaseColumnMapping.Source), nameof(DatabaseColumnMapping.TagId), nameof(DatabaseColumnMapping.FieldType), nameof(DatabaseColumnMapping.ConstantValue));

    internal static string CreateSql => """
        CREATE TABLE configuration_snapshots (
            slot TEXT PRIMARY KEY NOT NULL CHECK(slot IN ('current','previous')),
            schema_version INTEGER NOT NULL CHECK(schema_version=3),
            saved_ms INTEGER NOT NULL) STRICT;
        """ +
        Gateway.CreateSql +
        Telemetry.CreateSql +
        Mqtt.CreateSql +
        Rpc.CreateSql +
        Logging.CreateSql +
        Channels.CreateSql +
        Devices.CreateSql +
        Network.CreateSql +
        Serial.CreateSql +
        Tags.CreateSql +
        Databases.CreateSql +
        Groups.CreateSql +
        Columns.CreateSql +
        """
        CREATE TABLE device_properties (
            slot TEXT NOT NULL, device_id TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
            PRIMARY KEY(slot,device_id,name),
            FOREIGN KEY(slot,device_id) REFERENCES devices(slot,id) ON DELETE CASCADE ON UPDATE CASCADE) STRICT;
        CREATE TABLE database_write_group_tags (
            slot TEXT NOT NULL, group_id TEXT NOT NULL, position INTEGER NOT NULL CHECK(position>=0), tag_id TEXT NOT NULL,
            PRIMARY KEY(slot,group_id,tag_id), UNIQUE(slot,group_id,position),
            FOREIGN KEY(slot,group_id) REFERENCES database_write_groups(slot,id) ON DELETE CASCADE ON UPDATE CASCADE,
            FOREIGN KEY(slot,tag_id) REFERENCES tags(slot,id)) STRICT;
        CREATE INDEX devices_by_channel ON devices(slot,channel_id);
        CREATE INDEX groups_by_connection ON database_write_groups(slot,connection_id);
        CREATE INDEX mappings_by_tag ON database_column_mappings(slot,tag_id);
        CREATE INDEX group_selections_by_tag ON database_write_group_tags(slot,tag_id);
        """;

    // Capture scalar values before waiting for the writer. Caller mutations cannot alter a pending transaction.
    internal static List<Action<SqliteConnection, SqliteTransaction>> Capture(GatewayConfiguration configuration)
    {
        if (configuration.SchemaVersion != 3) throw new InvalidDataException("仅支持配置版本 3。");
        var rows = new List<Action<SqliteConnection, SqliteTransaction>>();
        void Add<T>(ConfigurationTable<T> table, T value, params (string Name, object Value)[] keys) where T : new()
        {
            var values = table.Capture(value);
            rows.Add((connection, transaction) => table.Insert(connection, transaction, "current", values, keys));
        }
        Add(Gateway, configuration);
        Add(Telemetry, configuration);
        Add(Mqtt, configuration.Mqtt);
        Add(Rpc, configuration.Mqtt);
        Add(Logging, configuration.Logging);
        for (var i = 0; i < configuration.Channels.Count; i++) Add(Channels, configuration.Channels[i], ("position", i));
        for (var i = 0; i < configuration.Devices.Count; i++)
        {
            var device = configuration.Devices[i];
            var id = device.Id.ToString("D");
            Add(Devices, device, ("position", i));
            Add(Network, device, ("device_id", id));
            Add(Serial, device, ("device_id", id));
            foreach (var pair in device.Properties)
            {
                var name = pair.Key;
                var value = pair.Value ?? throw new ArgumentException("驱动参数不能为空。");
                rows.Add((connection, transaction) => Execute(connection, transaction,
                    "INSERT INTO device_properties(slot,device_id,name,value) VALUES('current',$p0,$p1,$p2)", id, name, value));
            }
            for (var j = 0; j < device.Tags.Count; j++) Add(Tags, device.Tags[j], ("device_id", id), ("position", j));
        }
        for (var i = 0; i < configuration.Databases.Count; i++) Add(Databases, configuration.Databases[i], ("position", i));
        for (var i = 0; i < configuration.DatabaseWriteGroups.Count; i++)
        {
            var group = configuration.DatabaseWriteGroups[i];
            var id = group.Id.ToString("D");
            Add(Groups, group, ("position", i));
            for (var j = 0; j < group.TagIds.Count; j++)
            {
                var tag = group.TagIds[j].ToString("D");
                var position = j;
                rows.Add((connection, transaction) => Execute(connection, transaction,
                    "INSERT INTO database_write_group_tags(slot,group_id,position,tag_id) VALUES('current',$p0,$p1,$p2)", id, position, tag));
            }
            for (var j = 0; j < group.Columns.Count; j++) Add(Columns, group.Columns[j], ("group_id", id), ("position", j));
        }
        return rows;
    }

    internal static GatewayConfiguration? Read(SqliteConnection connection, string slot)
    {
        // All tables are read from the same SQLite snapshot, including when another process commits.
        using var transaction = connection.BeginTransaction(deferred: true);
        EnsureReferences(connection, transaction, slot);
        using var header = connection.CreateCommand();
        header.Transaction = transaction;
        header.CommandText = "SELECT schema_version FROM configuration_snapshots WHERE slot=$slot";
        header.Parameters.AddWithValue("$slot", slot);
        var version = header.ExecuteScalar();
        if (version is null)
        {
            if (slot == "current")
            {
                header.CommandText = "SELECT COUNT(*) FROM configuration_snapshots";
                if (Convert.ToInt32(header.ExecuteScalar()) != 0)
                    throw new InvalidDataException("配置数据库缺少当前快照；不会用默认配置覆盖已有快照。");
            }
            return null;
        }
        if (Convert.ToInt32(version) != 3) throw new InvalidDataException("配置快照版本无效；仅支持 schema 3。");
        var configuration = new GatewayConfiguration();
        Gateway.ReadRequired(connection, transaction, slot, configuration);
        Telemetry.ReadRequired(connection, transaction, slot, configuration);
        Mqtt.ReadRequired(connection, transaction, slot, configuration.Mqtt);
        Rpc.ReadRequired(connection, transaction, slot, configuration.Mqtt);
        Logging.ReadRequired(connection, transaction, slot, configuration.Logging);
        configuration.Channels = Channels.Read(connection, transaction, slot, "position");
        configuration.Devices = Devices.Read(connection, transaction, slot, "position");
        foreach (var device in configuration.Devices)
        {
            var parent = ("device_id", (object)device.Id.ToString("D"));
            Network.ReadRequired(connection, transaction, slot, device, parent);
            Serial.ReadRequired(connection, transaction, slot, device, parent);
            device.Tags = Tags.Read(connection, transaction, slot, "position", parent);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT name,value FROM device_properties WHERE slot=$slot AND device_id=$id ORDER BY name";
            command.Parameters.AddWithValue("$slot", slot);
            command.Parameters.AddWithValue("$id", device.Id.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) device.Properties.Add(reader.GetString(0), reader.GetString(1));
        }
        configuration.Databases = Databases.Read(connection, transaction, slot, "position");
        configuration.DatabaseWriteGroups = Groups.Read(connection, transaction, slot, "position");
        foreach (var group in configuration.DatabaseWriteGroups)
        {
            group.Columns = Columns.Read(connection, transaction, slot, "position", ("group_id", group.Id.ToString("D")));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT tag_id FROM database_write_group_tags WHERE slot=$slot AND group_id=$id ORDER BY position";
            command.Parameters.AddWithValue("$slot", slot);
            command.Parameters.AddWithValue("$id", group.Id.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) group.TagIds.Add(Guid.ParseExact(reader.GetString(0), "D"));
        }
        transaction.Commit();
        return configuration;
    }

    private static void EnsureReferences(SqliteConnection connection, SqliteTransaction transaction, string slot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check";
        var violations = new List<(string Table, long Row)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) violations.Add((reader.GetString(0), reader.GetInt64(1)));
        foreach (var (table, row) in violations)
        {
            // Quote the engine-reported identifier; never interpolate a field value into SQL.
            var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
            command.CommandText = $"SELECT slot FROM {quoted} WHERE rowid=$row";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$row", row);
            if (command.ExecuteScalar() as string == slot)
                throw new InvalidDataException($"配置表 {table} 存在失效关联；不会忽略记录或恢复默认配置。");
        }
    }

    internal static void Save(SqliteConnection connection, List<Action<SqliteConnection, SqliteTransaction>> rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        // Defer cross-entity references while the whole current snapshot moves to previous.
        Execute(connection, transaction, "PRAGMA defer_foreign_keys=ON;");
        Execute(connection, transaction, "DELETE FROM configuration_snapshots WHERE slot='previous';");
        Execute(connection, transaction, "UPDATE configuration_snapshots SET slot='previous' WHERE slot='current';");
        Execute(connection, transaction, "INSERT INTO configuration_snapshots(slot,schema_version,saved_ms) VALUES('current',3,$p0)", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            row(connection, transaction);
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params object[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
        command.ExecuteNonQuery();
    }
}
