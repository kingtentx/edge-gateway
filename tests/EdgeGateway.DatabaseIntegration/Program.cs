using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

// Explicit integration executable: missing services/credentials fail, never count as skipped or passed.
// It creates randomly named tables in the supplied dedicated test database and removes only those tables.
if (args.Length != 1 || args[0] is not ("mysql" or "postgres" or "oracle"))
    throw new ArgumentException("Usage: dotnet run --project tests/EdgeGateway.DatabaseIntegration -- mysql|postgres|oracle");
if (Environment.GetEnvironmentVariable("EDGE_DB_TEST_ALLOW_DDL") != "1")
    throw new InvalidOperationException("Set EDGE_DB_TEST_ALLOW_DDL=1 and select a dedicated test database before running.");
var provider = args[0] switch { "mysql" => ExternalDatabaseProvider.MySql, "postgres" => ExternalDatabaseProvider.PostgreSql, _ => ExternalDatabaseProvider.Oracle };
var configuration = new DatabaseConnectionConfiguration
{
    Provider = provider, Host = Required("EDGE_DB_TEST_HOST"), Port = int.Parse(Required("EDGE_DB_TEST_PORT"), CultureInfo.InvariantCulture),
    Database = Required("EDGE_DB_TEST_DATABASE"), Username = Required("EDGE_DB_TEST_USERNAME"),
    Password = Required("EDGE_DB_TEST_PASSWORD"), UseTls = Environment.GetEnvironmentVariable("EDGE_DB_TEST_TLS") == "1",
    ConnectionTimeoutSeconds = 5, CommandTimeoutSeconds = 10
};
var connector = new ExternalDatabaseConnector();
var prefix = "eg_it_" + Guid.NewGuid().ToString("N")[..12];
var schema = provider switch { ExternalDatabaseProvider.PostgreSql => "public", ExternalDatabaseProvider.Oracle => configuration.Username.ToUpperInvariant(), _ => configuration.Database };
var pointTable = new DatabaseTableInfo(schema, prefix + "_points");
var customTable = new DatabaseTableInfo(schema, prefix + "_custom");
var conflictTable = new DatabaseTableInfo(schema, prefix + "_conflict");
var created = new List<DatabaseTableInfo>();
var checks = new List<string>();
await using var raw = CreateRawConnection(configuration);
await raw.OpenAsync();
try
{
    var connectionTest = await connector.TestConnectionAsync(configuration);
    Check(connectionTest.Success, "connect");
    var pointGroup = new DatabaseWriteGroupConfiguration { Schema = schema, Table = pointTable.Name, Mode = DatabaseStorageMode.PointRows };
    var timestamp = new DateTimeOffset(2026, 10, 24, 14, 32, 10, TimeSpan.FromHours(8)).AddTicks(1234560);
    var device = Guid.NewGuid();
    var tag = Guid.NewGuid();
    var rows = Enumerable.Range(0, 4000).Select(index => PointRow(index, timestamp.AddMilliseconds(index), device, tag)).ToArray();

    await ExpectFailure(() => connector.WriteRowsAsync(configuration, pointGroup, [rows[0]]));
    Check(!(await connector.GetTablesAsync(configuration)).Contains(pointTable), "ordinary write never creates a missing table");
    await connector.EnsurePointTableAsync(configuration, pointTable);
    created.Add(pointTable);
    Check((await connector.GetTablesAsync(configuration)).Contains(pointTable), "table discovery");
    var columns = await connector.GetColumnsAsync(configuration, pointTable);
    Check(DatabasePointTable.Columns.All(column => columns.Any(metadata => metadata.Name == (provider == ExternalDatabaseProvider.Oracle ? column.ToUpperInvariant() : column))), "standard column discovery");
    await connector.EnsurePointTableAsync(configuration, pointTable);
    await connector.WriteRowsAsync(configuration, pointGroup, rows.Take(200).ToArray());
    var timer = Stopwatch.StartNew();
    foreach (var batch in rows.Skip(200).Chunk(200)) await connector.WriteRowsAsync(configuration, pointGroup, batch);
    timer.Stop();
    await connector.WriteRowsAsync(configuration, pointGroup, rows.Take(200).ToArray());
    Check(await Count(pointTable) == rows.Length, "stable event_id replay is idempotent");
    var history = await connector.QueryPointHistoryAsync(configuration, pointGroup, new(device, tag, timestamp.AddSeconds(-1), timestamp.AddSeconds(10), 10000));
    Check(history.Count == rows.Length, "filtered standard history");
    Check(history[^1].Timestamp.EqualsExact(timestamp.ToLocalTime()), "server local time and microsecond timestamp round trip");
    Check(history[^1].RawValue is long integer && integer == long.MaxValue, "64-bit raw value round trip");
    Check(history[^1].Value is JsonElement element && element.GetProperty("文本").GetString() == "温度; SELECT 'quoted';--", "structured JSON and Unicode round trip");
    Check((await connector.QueryPointHistoryAsync(configuration, pointGroup, new(device, Guid.NewGuid(), timestamp.AddSeconds(-1), timestamp.AddSeconds(10), 100))).Count == 0, "tag filter");
    Check((await connector.QueryPointHistoryAsync(configuration, pointGroup, new(device, tag, timestamp.AddSeconds(-1), timestamp.AddSeconds(10), 7))).Count == 7, "history limit");

    var identity = provider switch { ExternalDatabaseProvider.PostgreSql => "bigint GENERATED ALWAYS AS IDENTITY UNIQUE", ExternalDatabaseProvider.Oracle => "NUMBER GENERATED ALWAYS AS IDENTITY UNIQUE", _ => "bigint AUTO_INCREMENT UNIQUE" };
    var timeType = provider switch { ExternalDatabaseProvider.PostgreSql => "timestamp(6) without time zone", ExternalDatabaseProvider.Oracle => "TIMESTAMP(6)", _ => "datetime(6)" };
    var boolType = provider switch { ExternalDatabaseProvider.PostgreSql => "boolean", ExternalDatabaseProvider.Oracle => "NUMBER(1)", _ => "tinyint(1)" };
    await Execute($"CREATE TABLE {Table(customTable)} ({Q("row_id")} {identity}, {Q("event_key")} varchar(64) NOT NULL PRIMARY KEY, {Q("温度")} decimal(28,8) NOT NULL, {Q("at_local")} {timeType} NOT NULL, {Q("flag")} {boolType} NOT NULL, {Q("note")} varchar(120) NOT NULL, {Q("big_value")} decimal(20,0) NULL)" + (provider == ExternalDatabaseProvider.MySql ? " ENGINE=InnoDB DEFAULT CHARSET=utf8mb4" : ""));
    created.Add(customTable);
    var metadata = await connector.GetColumnsAsync(configuration, customTable);
    Check(metadata.Single(column => column.Name == "row_id").IsGenerated, "generated column metadata");
    var mappings = new[] { ("event_key", DatabaseFieldType.Text), ("温度", DatabaseFieldType.Decimal), ("at_local", DatabaseFieldType.Timestamp), ("flag", DatabaseFieldType.Boolean), ("note", DatabaseFieldType.Text), ("big_value", DatabaseFieldType.Decimal) };
    var customGroup = new DatabaseWriteGroupConfiguration { Schema = schema, Table = customTable.Name, Mode = DatabaseStorageMode.ColumnMapping,
        Columns = mappings.Select(mapping => new DatabaseColumnMapping { Column = mapping.Item1, FieldType = mapping.Item2 }).ToList() };
    var customRows = Enumerable.Range(0, 1001).Select(index => CustomRow(index, timestamp)).ToArray();
    var duplicate = CustomRow(0, timestamp);
    await ExpectFailure(() => connector.WriteRowsAsync(configuration, customGroup, customRows.Take(1000).Append(duplicate).ToArray()));
    Check(await Count(customTable) == 0, "later chunk constraint error rolls back earlier chunks");
    await connector.WriteRowsAsync(configuration, customGroup, customRows);
    Check(await Count(customTable) == customRows.Length, "custom mapped multi-row transaction");
    await using (var command = raw.CreateCommand())
    {
        command.CommandText = $"SELECT {Q("温度")}, {Q("at_local")}, {Q("flag")}, {Q("note")}, {Q("big_value")} FROM {Table(customTable)} WHERE {Q("event_key")} = 'custom-0'";
        await using var reader = await command.ExecuteReaderAsync();
        Check(await reader.ReadAsync(), "custom row read");
        Check(reader.GetDecimal(0) == 12345678901234567890.12345678m, "decimal precision");
        Check(reader.GetDateTime(1).Ticks == timestamp.ToLocalTime().Ticks, "custom timestamp acquisition-server clock");
        Check(provider == ExternalDatabaseProvider.Oracle ? reader.GetDecimal(2) == 1 : reader.GetBoolean(2), "boolean binding");
        Check(reader.GetString(3) == "温度'; DROP TABLE test;--", "SQL text remains a parameter value");
        Check(reader.GetDecimal(4) == (decimal)ulong.MaxValue, "unsigned 64-bit precision");
    }

    // A write locked by another session must cancel without committing a partial batch.
    await using (var lockConnection = CreateRawConnection(configuration))
    {
        await lockConnection.OpenAsync();
        await using var transaction = await lockConnection.BeginTransactionAsync();
        await using var held = lockConnection.CreateCommand();
        held.Transaction = transaction;
        // An UPDATE of an already committed row does not reliably block Oracle's
        // duplicate INSERT check. Hold a NEW unique key uncommitted instead, so
        // the second session must wait for this transaction on every provider.
        held.CommandText = $"INSERT INTO {Table(customTable)} ({Q("event_key")}, {Q("温度")}, {Q("at_local")}, {Q("flag")}, {Q("note")})"
            + $" SELECT 'custom-5000', {Q("温度")}, {Q("at_local")}, {Q("flag")}, {Q("note")} FROM {Table(customTable)} WHERE {Q("event_key")} = 'custom-0'";
        await held.ExecuteNonQueryAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await connector.WriteRowsAsync(configuration, customGroup, [CustomRow(2000, timestamp), CustomRow(5000, timestamp)], cancel.Token); throw new InvalidOperationException("Blocked write ignored cancellation"); }
        catch (OperationCanceledException) { Check(cancel.IsCancellationRequested, "blocked write cancellation"); }
        await transaction.RollbackAsync();
    }
    Check(await Count(customTable) == customRows.Length, "cancelled transaction leaves no new row");

    if (provider == ExternalDatabaseProvider.Oracle)
    {
        var fresh = PointRow(4001, timestamp, device, Guid.NewGuid());
        await connector.WriteRowsAsync(configuration, pointGroup, [rows[0], fresh]);
        await connector.WriteRowsAsync(configuration, pointGroup, [rows[0], fresh]);
        Check(await Count(pointTable) == rows.Length + 1, "mixed replay retains new rows exactly once");
        await connector.EnsurePointTableAsync(configuration, conflictTable);
        created.Add(conflictTable);
        var conflictGroup = new DatabaseWriteGroupConfiguration { Schema = schema, Table = conflictTable.Name };
        await connector.WriteRowsAsync(configuration, conflictGroup, [rows[0]]);
        await Execute($"ALTER TABLE {Table(conflictTable)} ADD CONSTRAINT {Q(prefix + "_ut")} UNIQUE ({Q("TAG_ID")})");
        await ExpectFailure(() => connector.WriteRowsAsync(configuration, conflictGroup, [fresh, rows[1]]));
        Check(await Count(conflictTable) == 1, "Oracle array replay never hides another business-key conflict or commits a partial chunk");
        var invalid = PointRow(4002, timestamp, device, Guid.NewGuid());
        var invalidValues = new Dictionary<string, object?>(invalid.Values) { ["timestamp"] = null };
        await ExpectFailure(() => connector.WriteRowsAsync(configuration, pointGroup, [PointRow(4003, timestamp, device, Guid.NewGuid()), invalid with { Values = invalidValues }]));
        Check(await Count(pointTable) == rows.Length + 1, "Oracle non-duplicate array failure rolls back all new rows");
    }

    if (provider == ExternalDatabaseProvider.MySql)
    {
        // Exercise the dangerous cached-schema window: another unique index must never hide a row.
        await connector.EnsurePointTableAsync(configuration, conflictTable);
        created.Add(conflictTable);
        var conflictGroup = new DatabaseWriteGroupConfiguration { Schema = schema, Table = conflictTable.Name };
        await connector.WriteRowsAsync(configuration, conflictGroup, [rows[0]]);
        await Execute($"ALTER TABLE {Table(conflictTable)} ADD UNIQUE KEY {Q("only_tag")} ({Q("tag_id")})");
        await ExpectFailure(() => connector.WriteRowsAsync(configuration, conflictGroup, [rows[1]]));
        Check(await Count(conflictTable) == 1, "cached MySQL metadata cannot hide a new business-key conflict");
        var oversized = CustomRow(3000, timestamp);
        var oversizedValues = new Dictionary<string, object?>(oversized.Values) { ["note"] = new string('x', 300) };
        await ExpectFailure(() => connector.WriteRowsAsync(configuration, customGroup, [oversized with { Values = oversizedValues }]));
        Check(await Count(customTable) == customRows.Length, "strict session rejects oversized text instead of truncating");
    }

    Console.WriteLine(JsonSerializer.Serialize(new { provider = provider.ToString(), serverVersion = connectionTest.ServerVersion, passed = checks.Count,
        checks, measuredRows = 3800, batchSize = 200, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
        rowsPerSecond = Math.Round(3800 / timer.Elapsed.TotalSeconds, 1), note = "Local-loopback functional smoke measurement; not an endurance or capacity guarantee." }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    foreach (var table in created.AsEnumerable().Reverse()) await Execute($"DROP TABLE {Table(table)}");
}

void Check(bool value, string description)
{ if (!value) throw new InvalidOperationException("FAIL: " + description); checks.Add(description); Console.Error.WriteLine("PASS: " + description); }
string Q(string identifier) => provider == ExternalDatabaseProvider.MySql ? "`" + identifier + "`" : "\"" + identifier + "\"";
string Table(DatabaseTableInfo table) => Q(table.Schema) + "." + Q(table.Name);
async Task Execute(string sql) { await using var command = raw.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
async Task<long> Count(DatabaseTableInfo table) { await using var command = raw.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + Table(table); return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture); }
static async Task ExpectFailure(Func<Task> action)
{
    try { await action(); }
    catch (InvalidOperationException exception) { if (exception.InnerException is not null) throw new InvalidOperationException("Provider exception leaked through connector", exception); return; }
    throw new InvalidOperationException("Expected operation failure, but it succeeded");
}
static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("Required test setting missing: " + name);
static DbConnection CreateRawConnection(DatabaseConnectionConfiguration configuration)
{
    var password = configuration.Password;
    if (configuration.Provider == ExternalDatabaseProvider.Oracle)
        return new OracleConnection(new OracleConnectionStringBuilder
        {
            DataSource = $"{configuration.Host}:{configuration.Port}/{configuration.Database}", UserID = configuration.Username,
            Password = password, Pooling = false, ConnectionTimeout = configuration.ConnectionTimeoutSeconds
        }.ConnectionString);
    return configuration.Provider == ExternalDatabaseProvider.MySql
        ? new MySqlConnection(new MySqlConnectionStringBuilder { Server = configuration.Host, Port = (uint)configuration.Port, Database = configuration.Database, UserID = configuration.Username, Password = password, Pooling = false, SslMode = configuration.UseTls ? MySqlSslMode.VerifyFull : MySqlSslMode.Disabled }.ConnectionString)
        : new NpgsqlConnection(new NpgsqlConnectionStringBuilder { Host = configuration.Host, Port = configuration.Port, Database = configuration.Database, Username = configuration.Username, Password = password, Pooling = false, SslMode = configuration.UseTls ? SslMode.VerifyFull : SslMode.Disable, Timezone = "Asia/Shanghai" }.ConnectionString);
}
static DatabaseWriteRow PointRow(int index, DateTimeOffset time, Guid device, Guid tag)
{
    var id = Guid.NewGuid().ToString("N");
    return new(id, time, new Dictionary<string, object?>
    {
        ["event_id"] = id, ["timestamp"] = time, ["device_id"] = device.ToString("D"), ["device_name"] = "验证设备", ["tag_id"] = tag.ToString("D"), ["tag_name"] = "温度", ["address"] = "0",
        ["raw_value"] = index == 0 ? JsonSerializer.Serialize(long.MaxValue) : JsonSerializer.Serialize(index), ["value_text"] = JsonSerializer.Serialize(new Dictionary<string, object?> { ["文本"] = "温度; SELECT 'quoted';--", ["index"] = index }),
        ["quality"] = "Good"
    });
}
static DatabaseWriteRow CustomRow(int index, DateTimeOffset time) => new(Guid.NewGuid().ToString("N"), time, new Dictionary<string, object?>
{
    ["event_key"] = "custom-" + index, ["温度"] = 12345678901234567890.12345678m, ["at_local"] = time, ["flag"] = index % 2 == 0,
    ["note"] = "温度'; DROP TABLE test;--", ["big_value"] = (decimal)ulong.MaxValue
});
