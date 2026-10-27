using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class ExternalDatabaseConnectorTests
{
    [Theory]
    [InlineData("samples;DROP_TABLE")]
    [InlineData("schema.table")]
    [InlineData("samples--")]
    [InlineData("table name")]
    [InlineData("\"quoted\"")]
    [InlineData("`quoted`")]
    [InlineData("a\n")]
    [InlineData("1table")]
    public void IdentifiersRejectSqlSyntaxForEveryProvider(string input)
    {
        foreach (var provider in Enum.GetValues<ExternalDatabaseProvider>())
            Assert.Throws<ArgumentException>(() => ExternalDatabaseConnector.QuoteIdentifier(provider, input));
    }

    [Fact]
    public void IdentifiersPreserveCaseAndApplyUtf8ByteLimits()
    {
        Assert.Equal("\"FACTORY\".\"Samples\"", ExternalDatabaseConnector.QualifiedTable(ExternalDatabaseProvider.Oracle, new("FACTORY", "Samples")));
        Assert.Equal("`设备`", ExternalDatabaseConnector.QuoteIdentifier(ExternalDatabaseProvider.MySql, "设备"));
        Assert.Equal("\"" + new string('温', 21) + "\"", ExternalDatabaseConnector.QuoteIdentifier(ExternalDatabaseProvider.PostgreSql, new string('温', 21)));
        Assert.Throws<ArgumentException>(() => ExternalDatabaseConnector.QuoteIdentifier(ExternalDatabaseProvider.PostgreSql, new string('温', 22)));
    }

    [Fact]
    public void StructuredConnectionStringsEscapeSecretsAndRequireCertificateValidation()
    {
        const string secret = "  temporary-test-value;Host=untrusted;Password=\"another\"  ";
        {
            var configuration = Configuration();
            configuration.Password = secret;
            configuration.UseTls = true;
            var pg = new NpgsqlConnectionStringBuilder(ExternalDatabaseConnector.BuildConnectionString(configuration));
            Assert.Equal(secret, pg.Password);
            Assert.Equal("localhost", pg.Host);
            Assert.Equal(SslMode.VerifyFull, pg.SslMode);
            Assert.False(pg.IncludeErrorDetail);
            configuration.Provider = ExternalDatabaseProvider.MySql;
            var mysql = new MySqlConnectionStringBuilder(ExternalDatabaseConnector.BuildConnectionString(configuration));
            Assert.Equal(secret, mysql.Password);
            Assert.Equal(MySqlSslMode.VerifyFull, mysql.SslMode);
            Assert.Equal(MySqlDateTimeKind.Unspecified, mysql.DateTimeKind);
            Assert.False(mysql.AllowLoadLocalInfile);
            Assert.False(mysql.AllowPublicKeyRetrieval);
            configuration.Provider = ExternalDatabaseProvider.Oracle;
            var oracle = new OracleConnectionStringBuilder(ExternalDatabaseConnector.BuildConnectionString(configuration));
            Assert.Equal(secret, oracle.Password);
            Assert.Contains("(PROTOCOL=TCPS)", oracle.DataSource);
            Assert.Contains("(SSL_SERVER_DN_MATCH=YES)", oracle.DataSource);
            Assert.Equal(secret, JsonSerializer.Deserialize<DatabaseConnectionConfiguration>(JsonSerializer.Serialize(configuration))!.Password);
        }
    }

    [Fact]
    public async Task InvalidPasswordConfigurationIsVisibleWithoutExposingAConnectionString()
    {
        var configuration = Configuration();
        configuration.Password = null!;
        var result = await new ExternalDatabaseConnector().TestConnectionAsync(configuration);
        Assert.False(result.Success);
        Assert.Contains("密码", result.Message);
        Assert.DoesNotContain("Password=", result.Message);
    }

    [Theory]
    [InlineData(ExternalDatabaseProvider.MySql, false)]
    [InlineData(ExternalDatabaseProvider.MySql, true)]
    [InlineData(ExternalDatabaseProvider.PostgreSql, false)]
    [InlineData(ExternalDatabaseProvider.PostgreSql, true)]
    [InlineData(ExternalDatabaseProvider.Oracle, false)]
    [InlineData(ExternalDatabaseProvider.Oracle, true)]
    public async Task ConnectionTestChecksDefaultTableAndNeverCreatesIt(ExternalDatabaseProvider provider, bool exists)
    {
        var database = new RecordingConnection { MetadataMode = exists ? "normal" : "missing" };
        var configuration = Configuration(); configuration.Provider = provider;
        var result = await new ExternalDatabaseConnector(_ => database).TestConnectionAsync(configuration);
        Assert.True(result.Success);
        Assert.Equal("test", result.ServerVersion);
        Assert.Contains(exists ? "已找到 gateway_point" : "未找到可访问的 gateway_point", result.Message);
        if (!exists) Assert.Contains("创建数据表", result.Message);
        var metadata = Assert.Single(database.Commands, command => command.Values.ContainsKey("eg_table_name"));
        Assert.Equal("gateway_point", metadata.Values["eg_table_name"]);
        Assert.Equal(DBNull.Value, metadata.Values["eg_schema"]);
        Assert.True(database.Commands.FindIndex(command => command.Sql.StartsWith("SELECT 1"))
            < database.Commands.IndexOf(metadata));
        Assert.DoesNotContain(database.Commands, command => command.Sql.StartsWith("CREATE ") || command.Sql.StartsWith("INSERT "));
        Assert.Equal(0, database.CommitCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionAndTableCheckFailuresAreDistinctAndRedacted(bool metadataFailure)
    {
        var database = new RecordingConnection { MetadataFailure = metadataFailure, ProbeFailure = !metadataFailure };
        var result = await new ExternalDatabaseConnector(_ => database).TestConnectionAsync(Configuration());
        Assert.False(result.Success);
        Assert.DoesNotContain("provider-secret", result.Message);
        if (metadataFailure) Assert.Contains("数据库连接成功，但默认表 gateway_point 检查失败", result.Message);
        else
        {
            Assert.DoesNotContain("数据库连接成功", result.Message);
            Assert.DoesNotContain(database.Commands, command => command.Values.ContainsKey("eg_table_name"));
        }
        Assert.DoesNotContain(database.Commands, command => command.Sql.StartsWith("CREATE "));
    }

    [Fact]
    public void OracleDescriptorCannotBeInjectedThroughStructuredFields()
    {
        var configuration = Configuration();
        configuration.Provider = ExternalDatabaseProvider.Oracle;
        configuration.Host = "localhost)(CONNECT_DATA=(SERVICE_NAME=other))";
        Assert.Throws<ArgumentException>(() => ExternalDatabaseConnector.ValidateConnection(configuration));
        configuration.Host = "localhost";
        configuration.Database = "service)(OTHER=payload)";
        Assert.Throws<ArgumentException>(() => ExternalDatabaseConnector.ValidateConnection(configuration));
    }

    [Fact]
    public void TimestampBindingsUseServerClockAndPreserveZonedInstants()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("AcquisitionServer", TimeSpan.FromHours(8), "Server", "Server");
        var source = new DateTimeOffset(2026, 10, 24, 14, 32, 10, TimeSpan.FromHours(8)).AddTicks(1234560);
        using var pg = new NpgsqlCommand();
        var zoned = (NpgsqlParameter)ExternalDatabaseConnector.AddValueParameter(pg, ExternalDatabaseProvider.PostgreSql, "a", source, databaseType: "timestamp with time zone");
        Assert.Equal(NpgsqlDbType.TimestampTz, zoned.NpgsqlDbType);
        Assert.Equal(source.UtcDateTime, zoned.Value);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)zoned.Value!).Kind);
        var unzoned = (NpgsqlParameter)ExternalDatabaseConnector.AddValueParameter(pg, ExternalDatabaseProvider.PostgreSql, "b", source, databaseType: "timestamp without time zone", serverTimeZone: zone);
        Assert.Equal(NpgsqlDbType.Timestamp, unzoned.NpgsqlDbType);
        Assert.Equal(source.DateTime.Ticks, ((DateTime)unzoned.Value!).Ticks);
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)unzoned.Value!).Kind);
        using var mysql = new MySqlCommand();
        Assert.Equal(source.DateTime, ExternalDatabaseConnector.AddValueParameter(mysql, ExternalDatabaseProvider.MySql, "a", source, serverTimeZone: zone).Value);
        using var oracle = new OracleCommand();
        var oracleUtc = (OracleParameter)ExternalDatabaseConnector.AddValueParameter(oracle, ExternalDatabaseProvider.Oracle, "a", source, databaseType: "TIMESTAMP(6)", serverTimeZone: zone);
        Assert.Equal(OracleDbType.TimeStamp, oracleUtc.OracleDbType);
        Assert.Equal(source.DateTime.Ticks, ((DateTime)oracleUtc.Value).Ticks);
        var oracleZoned = (OracleParameter)ExternalDatabaseConnector.AddValueParameter(oracle, ExternalDatabaseProvider.Oracle, "b", source, databaseType: "TIMESTAMP(6) WITH TIME ZONE", serverTimeZone: zone);
        Assert.Equal(OracleDbType.TimeStampTZ, oracleZoned.OracleDbType);
        Assert.Equal("+08:00", ((OracleTimeStampTZ)oracleZoned.Value).TimeZone);
        Assert.True(oracle.BindByName);
    }

    [Theory]
    [InlineData(480, "+08:00")]
    [InlineData(330, "+05:30")]
    [InlineData(-210, "-03:30")]
    public void SessionAndHistoryFollowAcquisitionServerInsteadOfSourceOffset(int minutes, string offset)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Server", TimeSpan.FromMinutes(minutes), "Server", "Server");
        var source = new DateTimeOffset(2026, 10, 26, 23, 30, 0, TimeSpan.Zero);
        var local = TimeZoneInfo.ConvertTime(source, zone);
        foreach (var provider in new[] { ExternalDatabaseProvider.MySql, ExternalDatabaseProvider.Oracle })
        {
            Assert.Contains("'" + offset + "'", ExternalDatabaseConnector.SessionTimeSql(provider, zone, source));
            var restored = ExternalDatabaseConnector.ReadPointTimestamp(local.DateTime, provider, zone);
            Assert.True(local.EqualsExact(restored));
        }
        Assert.True(local.EqualsExact(ExternalDatabaseConnector.ReadPointTimestamp(source.UtcDateTime, ExternalDatabaseProvider.PostgreSql, zone)));
        using var mysql = new MySqlCommand();
        var parameter = ExternalDatabaseConnector.AddValueParameter(mysql, ExternalDatabaseProvider.MySql, "time", source,
            databaseType: "datetime", serverTimeZone: zone);
        Assert.Equal(local.DateTime, parameter.Value);
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)parameter.Value!).Kind);
        var unspecified = ExternalDatabaseConnector.AddValueParameter(mysql, ExternalDatabaseProvider.MySql, "local", local.DateTime,
            databaseType: "datetime", serverTimeZone: zone);
        Assert.Equal(local.DateTime, unspecified.Value);
    }

    [Theory]
    [InlineData(ExternalDatabaseProvider.MySql)]
    [InlineData(ExternalDatabaseProvider.PostgreSql)]
    [InlineData(ExternalDatabaseProvider.Oracle)]
    public void ParameterBindingPreservesLargeIntegersDecimalsNullAndRejectsNonFiniteValues(ExternalDatabaseProvider provider)
    {
        using DbCommand command = provider switch
        {
            ExternalDatabaseProvider.MySql => new MySqlCommand(),
            ExternalDatabaseProvider.PostgreSql => new NpgsqlCommand(),
            _ => new OracleCommand()
        };
        Assert.Equal(long.MaxValue, ExternalDatabaseConnector.AddValueParameter(command, provider, "signed", long.MaxValue).Value);
        Assert.Equal((decimal)ulong.MaxValue, ExternalDatabaseConnector.AddValueParameter(command, provider, "unsigned", ulong.MaxValue).Value);
        var unsigned32 = ExternalDatabaseConnector.AddValueParameter(command, provider, "unsigned32", uint.MaxValue).Value;
        Assert.Equal((decimal)uint.MaxValue, Convert.ToDecimal(unsigned32));
        const decimal exact = 12345678901234567890.12345678m;
        Assert.Equal(exact, ExternalDatabaseConnector.AddValueParameter(command, provider, "decimal", exact).Value);
        Assert.Equal(DBNull.Value, ExternalDatabaseConnector.AddValueParameter(command, provider, "null", null, DatabaseFieldType.Double).Value);
        Assert.Throws<ArgumentException>(() => ExternalDatabaseConnector.AddValueParameter(command, provider, "infinite", double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => ExternalDatabaseConnector.AddValueParameter(command, provider, "nan", float.NaN));
    }

    [Fact]
    public void OracleTextUsesNclobAndBooleansSupportNumberAndNativeBooleanColumns()
    {
        using var command = new OracleCommand();
        var text = new string('温', 6000);
        var longText = (OracleParameter)ExternalDatabaseConnector.AddValueParameter(command, ExternalDatabaseProvider.Oracle, "text", text, databaseType: "NCLOB");
        Assert.Equal(OracleDbType.NClob, longText.OracleDbType);
        Assert.Equal(text, longText.Value);
        var numericBoolean = (OracleParameter)ExternalDatabaseConnector.AddValueParameter(command, ExternalDatabaseProvider.Oracle, "flag", true, databaseType: "NUMBER");
        Assert.Equal(OracleDbType.Int16, numericBoolean.OracleDbType);
        Assert.Equal((short)1, numericBoolean.Value);
        var nativeBoolean = (OracleParameter)ExternalDatabaseConnector.AddValueParameter(command, ExternalDatabaseProvider.Oracle, "native", false, databaseType: "BOOLEAN");
        Assert.Equal(OracleDbType.Boolean, nativeBoolean.OracleDbType);
        Assert.Equal(false, nativeBoolean.Value);
    }

    [Fact]
    public void StandardSqlUsesTypedUtcColumnsAndConflictTargetsOnlyTheStableEventId()
    {
        var table = new DatabaseTableInfo("factory", "samples");
        var pg = ExternalDatabaseConnector.BuildInsertSql(ExternalDatabaseProvider.PostgreSql, table, DatabasePointTable.Columns, 2, true);
        Assert.Contains("ON CONFLICT (\"event_id\") DO NOTHING", pg);
        Assert.Contains("@p1_9", pg);
        Assert.DoesNotContain("@p1_10", pg);
        var mysql = ExternalDatabaseConnector.BuildInsertSql(ExternalDatabaseProvider.MySql, table, DatabasePointTable.Columns, 2, true);
        Assert.Contains("ON DUPLICATE KEY UPDATE `event_id` = IF(`factory`.`samples`.`event_id` = `eg_incoming`.`event_id`, `factory`.`samples`.`event_id`, NULL)", mysql);
        Assert.DoesNotContain("IGNORE", mysql);
        Assert.Contains("timestamp with time zone", ExternalDatabaseConnector.BuildCreatePointTableSql(ExternalDatabaseProvider.PostgreSql, table));
        Assert.Contains("datetime(6)", ExternalDatabaseConnector.BuildCreatePointTableSql(ExternalDatabaseProvider.MySql, table));
        Assert.Contains("\"TIMESTAMP\" timestamp(6)", ExternalDatabaseConnector.BuildCreatePointTableSql(ExternalDatabaseProvider.Oracle, table));
        foreach (var provider in Enum.GetValues<ExternalDatabaseProvider>())
        {
            var ddl = ExternalDatabaseConnector.BuildCreatePointTableSql(provider, table);
            foreach (var field in new[] { "device_name", "tag_name", "address", "raw_value", "value_text" })
                Assert.Contains(provider == ExternalDatabaseProvider.Oracle
                    ? $"\"{field.ToUpperInvariant()}\" VARCHAR2(128 CHAR)"
                    : ExternalDatabaseConnector.QuoteIdentifier(provider, field) + " varchar(128)", ddl);
            Assert.Contains(provider == ExternalDatabaseProvider.Oracle ? "\"DEVICE_ID\" VARCHAR2(36)" : ExternalDatabaseConnector.QuoteIdentifier(provider, "device_id") + " varchar(36)", ddl);
            Assert.DoesNotContain("numeric_value", ddl, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("ON CONFLICT", ExternalDatabaseConnector.BuildInsertSql(ExternalDatabaseProvider.PostgreSql, table, ["business_value"], 1, false));
    }

    [Fact]
    public async Task CustomBatchIsParameterizedAndCommitsAllChunksInOneTransaction()
    {
        var database = new RecordingConnection();
        var connector = new ExternalDatabaseConnector(_ => database);
        var group = MappedGroup();
        var rows = Enumerable.Range(0, 1001).Select(index => Row("value-'" + index + "; DROP TABLE business;--")).ToArray();
        await connector.WriteRowsAsync(Configuration(), group, rows);
        var writes = database.Commands.Where(command => command.Sql.StartsWith("INSERT ")).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(1000, writes[0].Values.Count);
        Assert.Single(writes[1].Values);
        Assert.All(writes, command =>
        {
            Assert.DoesNotContain("DROP TABLE", command.Sql);
            Assert.True(command.InTransaction);
        });
        Assert.Contains("DROP TABLE", Assert.IsType<string>(writes[0].Values["p0_0"]));
        Assert.Equal(1, database.CommitCount);
        Assert.Equal(0, database.RollbackCount);
        Assert.DoesNotContain(database.Commands, command => command.Sql.Contains("CREATE ") || command.Sql.Contains("ALTER "));
    }

    [Fact]
    public async Task FailureInLaterChunkRollsBackAndRemovesSensitiveProviderDiagnostics()
    {
        var database = new RecordingConnection { FailWriteNumber = 2 };
        var connector = new ExternalDatabaseConnector(_ => database);
        var exception = await Assert.ThrowsAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() => connector.WriteRowsAsync(Configuration(), MappedGroup(), Enumerable.Range(0, 1001).Select(_ => Row("sample-secret")).ToArray()));
        Assert.Equal(0, database.CommitCount);
        Assert.Equal(1, database.RollbackCount);
        Assert.DoesNotContain("sample-secret", exception.ToString());
        Assert.DoesNotContain("Password", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task MissingOrGeneratedBusinessColumnsFailBeforeAnyWriteOrDdl()
    {
        foreach (var mode in new[] { "missing", "generated", "unmapped" })
        {
            var database = new RecordingConnection { MetadataMode = mode };
            var connector = new ExternalDatabaseConnector(_ => database);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => connector.WriteRowsAsync(Configuration(), MappedGroup(), [Row("sample")]));
            Assert.DoesNotContain(database.Commands, command => command.Sql.StartsWith("INSERT ") || command.Sql.Contains("CREATE ") || command.Sql.Contains("ALTER "));
            Assert.Equal(0, database.CommitCount);
        }
    }

    [Fact]
    public async Task CallerCancellationDuringWriteRollsBackAndRemainsCancellation()
    {
        var database = new RecordingConnection { BlockWrite = true };
        var connector = new ExternalDatabaseConnector(_ => database);
        using var cancellation = new CancellationTokenSource();
        var write = connector.WriteRowsAsync(Configuration(), MappedGroup(), [Row("sample")], cancellation.Token);
        await database.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(0, database.CommitCount);
        Assert.Equal(1, database.RollbackCount);
    }

    [Fact]
    public async Task CommandDeadlineDoesNotWaitForAnUnboundedProviderOperation()
    {
        var database = new RecordingConnection { BlockWrite = true };
        var connector = new ExternalDatabaseConnector(_ => database);
        var configuration = Configuration();
        configuration.CommandTimeoutSeconds = 1;
        await Assert.ThrowsAsync<TimeoutException>(() => connector.WriteRowsAsync(configuration, MappedGroup(), [Row("sample")]).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, database.RollbackCount);
    }

    [Fact]
    public async Task MetadataIsReusedAcrossBatchesAndRevalidatedImmediatelyAfterWriteFailure()
    {
        var database = new RecordingConnection { FailWriteNumber = 2 };
        var connector = new ExternalDatabaseConnector(_ => database);
        await connector.WriteRowsAsync(Configuration(), MappedGroup(), [Row("one")]);
        await Assert.ThrowsAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() => connector.WriteRowsAsync(Configuration(), MappedGroup(), [Row("two")]));
        Assert.Single(database.Commands, command => command.Sql.Contains("information_schema.columns"));
        await connector.WriteRowsAsync(Configuration(), MappedGroup(), [Row("three")]);
        Assert.Equal(2, database.Commands.Count(command => command.Sql.Contains("information_schema.columns")));
    }

    [Fact]
    public void OracleArrayBindingCarriesTypedValuesNullStatusesAndVariableTextSizes()
    {
        using var command = new OracleCommand();
        var group = new DatabaseWriteGroupConfiguration { Mode = DatabaseStorageMode.ColumnMapping,
            Columns = [new() { Column = "VALUE", FieldType = DatabaseFieldType.Double }, new() { Column = "TEXT", FieldType = DatabaseFieldType.Text }] };
        var rows = new[]
        {
            new DatabaseWriteRow("one", DateTimeOffset.UtcNow, new Dictionary<string, object?> { ["VALUE"] = null, ["TEXT"] = "温度" }),
            new DatabaseWriteRow("two", DateTimeOffset.UtcNow, new Dictionary<string, object?> { ["VALUE"] = 42.5d, ["TEXT"] = new string('温', 4000) })
        };
        var metadata = new Dictionary<string, DatabaseColumnInfo> { ["VALUE"] = new("VALUE", "BINARY_DOUBLE", true), ["TEXT"] = new("TEXT", "NCLOB", true) };
        Assert.True(ExternalDatabaseConnector.TryBindOracleArray(command, group, ["VALUE", "TEXT"], rows, 0, 2, metadata));
        Assert.Equal(2, command.ArrayBindCount);
        Assert.True(command.BindByName);
        Assert.Equal([0d, 42.5d], Assert.IsType<double[]>(command.Parameters[0].Value));
        Assert.Equal(OracleParameterStatus.NullInsert, command.Parameters[0].ArrayBindStatus[0]);
        Assert.Equal(OracleParameterStatus.Success, command.Parameters[0].ArrayBindStatus[1]);
        Assert.Equal(OracleDbType.NClob, command.Parameters[1].OracleDbType);
        Assert.Equal([2, 4000], command.Parameters[1].ArrayBindSize);
    }

    [Fact]
    public void OracleArrayBindingDeclinesLossyCoercionOfMixedAutoTypes()
    {
        using var command = new OracleCommand();
        var group = new DatabaseWriteGroupConfiguration { Mode = DatabaseStorageMode.ColumnMapping, Columns = [new() { Column = "V" }] };
        var rows = new[]
        {
            new DatabaseWriteRow("one", DateTimeOffset.UtcNow, new Dictionary<string, object?> { ["V"] = long.MaxValue }),
            new DatabaseWriteRow("two", DateTimeOffset.UtcNow, new Dictionary<string, object?> { ["V"] = 1.25d })
        };
        Assert.False(ExternalDatabaseConnector.TryBindOracleArray(command, group, ["V"], rows, 0, 2,
            new Dictionary<string, DatabaseColumnInfo> { ["V"] = new("V", "NUMBER", true) }));
    }

    [Fact]
    public void OracleArrayErrorsCannotTreatUnrelatedErrorsOrInvalidIndicesAsReplays()
    {
        Assert.Equal([0, 2], Assert.IsType<int[]>(ExternalDatabaseConnector.ReplayIndices([(1, 0), (1, 2), (1, 2)], 3)));
        Assert.Null(ExternalDatabaseConnector.ReplayIndices([(1, 0), (1400, 1)], 3));
        Assert.Null(ExternalDatabaseConnector.ReplayIndices([(1, -1)], 3));
        Assert.Null(ExternalDatabaseConnector.ReplayIndices([(1, 3)], 3));
        Assert.Null(ExternalDatabaseConnector.ReplayIndices([], 3));
    }

    [Fact]
    public async Task HistoryIsBoundedFilteredAndRestoresTypedJsonInUtc()
    {
        var database = new RecordingConnection { History = true };
        var connector = new ExternalDatabaseConnector(_ => database);
        var instant = new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.FromHours(8));
        var device = Guid.NewGuid();
        var tag = Guid.NewGuid();
        var query = new HistoryQuery(device, tag, instant.AddHours(-1), instant.AddHours(1), int.MaxValue);
        var result = Assert.Single(await connector.QueryPointHistoryAsync(Configuration(), new() { Table = "samples" }, query));
        Assert.Equal(long.MaxValue, result.RawValue);
        Assert.Equal(42, Assert.IsType<JsonElement>(result.Value).GetProperty("v").GetInt32());
        Assert.Equal(instant.ToLocalTime().Offset, result.Timestamp.Offset);
        var command = Assert.Single(database.Commands);
        Assert.Equal(10000, command.Values["row_limit"]);
        Assert.Equal(instant.AddHours(-1).UtcDateTime, command.Values["from_time"]);
        Assert.Equal(device.ToString("D"), command.Values["device"]);
        Assert.Contains("\"timestamp\" >= @from_time", command.Sql);
        Assert.Equal(device, result.DeviceId);
        Assert.Equal(tag, result.TagId);
        Assert.Equal("0", result.Address);
        Assert.Equal("", result.Unit);
        Assert.Null(result.Error);
        Assert.Equal(tag.ToString("D"), command.Values["tag"]);
        Assert.DoesNotContain(device.ToString("D"), command.Sql);
        await Assert.ThrowsAsync<ArgumentException>(() => connector.QueryPointHistoryAsync(Configuration(), new() { Mode = DatabaseStorageMode.ColumnMapping }, query));
    }

    [Theory]
    [InlineData("device_name")]
    [InlineData("tag_name")]
    [InlineData("address")]
    [InlineData("raw_value")]
    [InlineData("value_text")]
    public async Task OversizedStandardTextRejectsWholeBatchBeforeOpeningDatabase(string column)
    {
        var database = new RecordingConnection();
        var connector = new ExternalDatabaseConnector(_ => database);
        var valid = StandardRow();
        var invalid = valid with { Values = new Dictionary<string, object?>(valid.Values) { [column] = new string('温', 129) } };
        var error = await Assert.ThrowsAnyAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() =>
            connector.WriteRowsAsync(Configuration(), new(), [valid, invalid]));
        Assert.Contains(column, error.Message);
        Assert.False(error.IsTransient);
        Assert.DoesNotContain(new string('温', 129), error.Message);
        Assert.Empty(database.Commands);
        Assert.Equal(ConnectionState.Closed, database.State);
    }

    [Fact]
    public async Task ExplicitCreationBuildsIndexesBeforeWritingAndReusesTheTable()
    {
        var database = new RecordingConnection { MetadataMode = "missing" };
        var connector = new ExternalDatabaseConnector(_ => database);
        await connector.EnsurePointTableAsync(Configuration(), new("", "gateway_point"));
        await connector.WriteRowsAsync(Configuration(), new(), [StandardRow()]);
        await connector.WriteRowsAsync(Configuration(), new(), [StandardRow()]);
        var create = Assert.Single(database.Commands, command => command.Sql.StartsWith("CREATE TABLE "));
        Assert.Contains("\"gateway_point\"", create.Sql);
        Assert.False(create.InTransaction);
        var indexes = database.Commands.Where(command => command.Sql.StartsWith("CREATE INDEX ")).ToArray();
        Assert.Equal(2, indexes.Length);
        Assert.All(indexes, command => Assert.False(command.InTransaction));
        Assert.All(database.Commands.Where(command => command.Sql.StartsWith("INSERT ")), command => Assert.True(command.InTransaction));
        Assert.True(database.Commands.FindIndex(command => command.Sql.StartsWith("CREATE INDEX "))
            < database.Commands.FindIndex(command => command.Sql.StartsWith("INSERT ")));
        Assert.Equal(2, database.CommitCount);
    }

    [Fact]
    public async Task ExplicitCreationPermissionFailureDoesNotWriteAndRemainsRedacted()
    {
        var database = new RecordingConnection { MetadataMode = "missing", CreateFailure = "42501" };
        var connector = new ExternalDatabaseConnector(_ => database);
        var error = await Assert.ThrowsAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() =>
            connector.EnsurePointTableAsync(Configuration(), new("", "gateway_point")));
        Assert.Contains("42501", error.Message);
        Assert.DoesNotContain("provider-secret", error.Message);
        Assert.False(error.IsTransient);
        Assert.DoesNotContain(database.Commands, command => command.Sql.StartsWith("INSERT "));
        Assert.Equal(0, database.CommitCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCreationChecksAConcurrentCreatorsTable(bool incompatible)
    {
        var database = new RecordingConnection
        { MetadataMode = "missing", CreateFailure = "42P07", ConcurrentTableIncompatible = incompatible };
        var connector = new ExternalDatabaseConnector(_ => database);
        var write = connector.EnsurePointTableAsync(Configuration(), new("", "gateway_point"));
        if (incompatible)
        {
            await Assert.ThrowsAnyAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() => write);
            Assert.DoesNotContain(database.Commands, command => command.Sql.StartsWith("INSERT "));
        }
        else
        {
            await write;
            await connector.WriteRowsAsync(Configuration(), new(), [StandardRow()]);
            Assert.Equal(1, database.CommitCount);
        }
        Assert.DoesNotContain(database.Commands, command => command.Sql.StartsWith("CREATE INDEX ") || command.Sql.StartsWith("ALTER TABLE "));
    }

    [Fact]
    public async Task ExistingIncompatibleDefaultTableIsNotAlteredOrWritten()
    {
        var database = new RecordingConnection(); // Existing business columns, not a point table.
        var connector = new ExternalDatabaseConnector(_ => database);
        await Assert.ThrowsAnyAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() =>
            connector.WriteRowsAsync(Configuration(), new(), [StandardRow()]));
        Assert.DoesNotContain(database.Commands, command => command.Sql.StartsWith("CREATE ")
            || command.Sql.StartsWith("ALTER ") || command.Sql.StartsWith("INSERT "));
    }

    [Theory]
    [InlineData("gateway_point")]
    [InlineData("business_points")]
    public async Task MissingPointTableRequiresExplicitCreation(string table)
    {
        var database = new RecordingConnection { MetadataMode = "missing" };
        var connector = new ExternalDatabaseConnector(_ => database);
        await Assert.ThrowsAnyAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() =>
            connector.WriteRowsAsync(Configuration(), new() { Table = table }, [StandardRow()]));
        Assert.Single(database.Commands);
        Assert.DoesNotContain(database.Commands, command => command.Sql.StartsWith("CREATE "));
    }

    [Fact]
    public async Task CustomPointModeUsesMappedColumnsAndDoesNotCreateAMissingBusinessTable()
    {
        var group = MappedGroup(); group.Mode = DatabaseStorageMode.PointMapping;
        var database = new RecordingConnection();
        await new ExternalDatabaseConnector(_ => database).WriteRowsAsync(Configuration(), group, [Row("value")]);
        Assert.Equal(1, database.CommitCount);
        var insert = Assert.Single(database.Commands, command => command.Sql.StartsWith("INSERT "));
        Assert.Contains("\"value\"", insert.Sql);
        Assert.DoesNotContain("event_id", insert.Sql);
        Assert.True(insert.InTransaction);
        var missing = new RecordingConnection { MetadataMode = "missing" };
        group.Table = "gateway_point"; // Default naming never authorizes creation.
        await Assert.ThrowsAnyAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() =>
            new ExternalDatabaseConnector(_ => missing).WriteRowsAsync(Configuration(), group, [Row("value")]));
        Assert.DoesNotContain(missing.Commands, command => command.Sql.StartsWith("CREATE ") || command.Sql.StartsWith("INSERT "));
    }

    [Fact]
    public async Task DefaultNameInCustomMappingModeDoesNotCreateATable()
    {
        var database = new RecordingConnection { MetadataMode = "missing" };
        var connector = new ExternalDatabaseConnector(_ => database);
        var group = MappedGroup();
        group.Table = "gateway_point";
        await Assert.ThrowsAnyAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() =>
            connector.WriteRowsAsync(Configuration(), group, [Row("sample")]));
        Assert.Single(database.Commands);
        Assert.DoesNotContain(database.Commands, command => command.Sql.StartsWith("CREATE "));
    }

    [Fact]
    public async Task EmptyOrCancelledBatchDoesNotCreateATable()
    {
        var database = new RecordingConnection { MetadataMode = "missing" };
        var connector = new ExternalDatabaseConnector(_ => database);
        await connector.WriteRowsAsync(Configuration(), new(), []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connector.WriteRowsAsync(Configuration(), new(), [StandardRow()], cancellation.Token));
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task StandardTextAllows128UnicodeCharactersBeforeMetadataValidation()
    {
        var database = new RecordingConnection { MetadataMode = "missing" };
        var connector = new ExternalDatabaseConnector(_ => database);
        var row = StandardRow();
        var values = new Dictionary<string, object?>(row.Values);
        foreach (var column in new[] { "device_name", "tag_name", "address", "raw_value", "value_text" })
            values[column] = string.Concat(Enumerable.Repeat("😀", 128));
        var error = await Assert.ThrowsAnyAsync<EdgeGateway.Abstractions.DatabaseWriteException>(() =>
            connector.WriteRowsAsync(Configuration(), new() { Table = "business_points" }, [row with { Values = values }]));
        Assert.Contains("目标数据表不存在", error.Message);
        Assert.Single(database.Commands);
    }

    private static DatabaseConnectionConfiguration Configuration() => new()
    { Provider = ExternalDatabaseProvider.PostgreSql, Username = "edge_test", Database = "edge_test", Port = 5432 };

    private static DatabaseWriteRow StandardRow() => new("event", DateTimeOffset.UtcNow, new Dictionary<string, object?>
    {
        ["event_id"] = "event", ["device_id"] = Guid.NewGuid().ToString("D"), ["device_name"] = "设备",
        ["tag_id"] = Guid.NewGuid().ToString("D"), ["tag_name"] = "点", ["address"] = "0",
        ["raw_value"] = "1", ["value_text"] = "1", ["timestamp"] = DateTimeOffset.UtcNow, ["quality"] = "Good"
    });

    private static DatabaseWriteGroupConfiguration MappedGroup() => new()
    { Mode = DatabaseStorageMode.ColumnMapping, Table = "business", Columns = [new() { Column = "value", FieldType = DatabaseFieldType.Text }] };

    private static DatabaseWriteRow Row(string value) => new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, new Dictionary<string, object?> { ["value"] = value });

    private sealed record CapturedCommand(string Sql, Dictionary<string, object?> Values, bool InTransaction);

    private sealed class RecordingConnection : DbConnection
    {
        public List<CapturedCommand> Commands { get; } = [];
        public int CommitCount { get; set; }
        public int RollbackCount { get; set; }
        public int FailWriteNumber { get; init; }
        public bool BlockWrite { get; init; }
        public bool History { get; init; }
        public string MetadataMode { get; init; } = "normal";
        public string? CreateFailure { get; init; }
        public bool MetadataFailure { get; init; }
        public bool ProbeFailure { get; init; }
        public bool ConcurrentTableIncompatible { get; init; }
        private bool _standardTableCreated;
        private bool _incompatibleTableCreated;
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        private ConnectionState _state;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "edge_test";
        public override string DataSource => "loopback-test";
        public override string ServerVersion => "test";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new RecordingTransaction(this);
        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);
        public override Task OpenAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Open(); return Task.CompletedTask; }

        private void Capture(RecordingCommand command) => Commands.Add(new(command.CommandText,
            command.Parameters.Cast<DbParameter>().ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value), command.Transaction is not null));

        public async Task<int> NonQuery(RecordingCommand command, CancellationToken token)
        {
            Capture(command);
            if (command.CommandText.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
            {
                if (CreateFailure is null or "42P07")
                {
                    _standardTableCreated = !ConcurrentTableIncompatible;
                    _incompatibleTableCreated = ConcurrentTableIncompatible;
                }
                if (CreateFailure is not null) throw new PostgresException("provider-secret", "ERROR", "ERROR", CreateFailure);
            }
            if (!command.CommandText.StartsWith("INSERT ", StringComparison.Ordinal)) return 1;
            WriteStarted.TrySetResult();
            if (BlockWrite) await Task.Delay(Timeout.Infinite, token);
            if (++_writes == FailWriteNumber) throw new InvalidOperationException("Password=provider-secret; SQL INSERT sample-secret");
            return 1;
        }

        public object Scalar(RecordingCommand command)
        {
            Capture(command);
            if (ProbeFailure) throw new PostgresException("provider-secret", "ERROR", "ERROR", "08006");
            if (command.CommandText is not ("SELECT 1" or "SELECT 1 FROM DUAL")) throw new NotSupportedException();
            return 1;
        }

        public DbDataReader Read(RecordingCommand command)
        {
            Capture(command);
            if (MetadataFailure) throw new PostgresException("provider-secret", "ERROR", "ERROR", "42501");
            var table = new DataTable();
            if (_standardTableCreated && command.CommandText.Contains("information_schema.table_constraints"))
            {
                table.Columns.Add("constraint_name", typeof(string));
                table.Columns.Add("column_name", typeof(string));
                table.Rows.Add("gateway_point_pkey", "event_id");
            }
            else if (_standardTableCreated)
            {
                for (var index = 0; index < 6; index++) table.Columns.Add("c" + index, typeof(string));
                foreach (var column in DatabasePointTable.Columns)
                    table.Rows.Add(column, column == "timestamp" ? "timestamp with time zone" : "character varying", "NO", "N", "NO", "NEVER");
            }
            else if (History)
            {
                for (var index = 0; index < 10; index++) table.Columns.Add("c" + index, index == 8 ? typeof(DateTime) : typeof(string));
                table.Rows.Add("event", command.Parameters["device"].Value, "设备", command.Parameters["tag"].Value, "点", "0", long.MaxValue.ToString(), "{\"v\":42}", new DateTime(2026, 10, 23, 23, 0, 0), "Good");
            }
            else
            {
                for (var index = 0; index < 6; index++) table.Columns.Add("c" + index, typeof(string));
                if (MetadataMode != "missing" || _incompatibleTableCreated) table.Rows.Add("value", "text", "NO", "N", "NO", MetadataMode == "generated" ? "ALWAYS" : "NEVER");
                if (MetadataMode == "unmapped") table.Rows.Add("required", "text", "NO", "N", "NO", "NEVER");
            }
            return table.CreateDataReader();
        }
    }

    private sealed class RecordingTransaction(RecordingConnection database) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => database;
        public override void Commit() => database.CommitCount++;
        public override void Rollback() => database.RollbackCount++;
    }

    private sealed class RecordingCommand(RecordingConnection database) : DbCommand
    {
        private readonly NpgsqlCommand _parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = database;
        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new NpgsqlParameter();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<object?>(database.Scalar(this));
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => database.Read(this);
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => database.NonQuery(this, cancellationToken);
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(database.Read(this)); }
        protected override void Dispose(bool disposing) { if (disposing) _parameters.Dispose(); base.Dispose(disposing); }
    }
}
