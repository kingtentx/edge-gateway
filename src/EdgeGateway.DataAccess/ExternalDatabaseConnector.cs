using System.Data;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace EdgeGateway.DataAccess;

/// <summary>
/// External acquisition storage. No method except the explicit EnsurePointTable action executes DDL.
/// UTC instants are stored as PostgreSQL timestamptz, MySQL datetime(6), and Oracle timestamp(6).
/// Standard point rows deduplicate immutable event_id values; custom mappings use ordinary INSERT semantics.
/// </summary>
public sealed partial class ExternalDatabaseConnector : IExternalDatabaseConnector
{
    private readonly Func<DatabaseConnectionConfiguration, DbConnection> _connectionFactory;
    private readonly ConcurrentDictionary<string, ValidatedTable> _validatedTables = new(StringComparer.Ordinal);
    private sealed record ValidatedTable(IReadOnlyList<DatabaseColumnInfo> Columns, long ExpiresAt);
    public ExternalDatabaseConnector() : this(CreateConnection) { }
    internal ExternalDatabaseConnector(Func<DatabaseConnectionConfiguration, DbConnection> connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<DatabaseConnectionTestResult> TestConnectionAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default)
    {
        try
        {
            return await WithConnectionAsync(connection, async (database, token) =>
            {
                await using var command = Command(database, connection, connection.Provider == ExternalDatabaseProvider.Oracle ? "SELECT 1 FROM DUAL" : "SELECT 1");
                await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return new DatabaseConnectionTestResult(true, "数据库连接成功。", database.ServerVersion);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new(false, exception.Message); }
    }

    public Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(DatabaseConnectionConfiguration connection, CancellationToken cancellationToken = default) =>
        WithConnectionAsync<IReadOnlyList<DatabaseTableInfo>>(connection, async (database, token) =>
        {
            var sql = connection.Provider switch
            {
                ExternalDatabaseProvider.MySql => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema = @database AND table_type = 'BASE TABLE' ORDER BY table_schema, table_name LIMIT 2000",
                ExternalDatabaseProvider.PostgreSql => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('information_schema', 'pg_catalog') ORDER BY table_schema, table_name LIMIT 2000",
                ExternalDatabaseProvider.Oracle => "SELECT owner, table_name FROM all_tables WHERE owner NOT IN ('SYS', 'SYSTEM', 'XDB', 'MDSYS', 'CTXSYS', 'ORDSYS', 'WMSYS', 'DBSNMP') ORDER BY owner, table_name FETCH FIRST 2000 ROWS ONLY",
                _ => throw new ArgumentException("不支持的数据库类型。")
            };
            await using var command = Command(database, connection, sql);
            if (connection.Provider == ExternalDatabaseProvider.MySql) AddValueParameter(command, connection.Provider, "database", connection.Database);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var tables = new List<DatabaseTableInfo>();
            while (await reader.ReadAsync(token).ConfigureAwait(false)) tables.Add(new(reader.GetString(0), reader.GetString(1)));
            return tables;
        }, cancellationToken);

    public Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(DatabaseConnectionConfiguration connection, DatabaseTableInfo table,
        CancellationToken cancellationToken = default)
    {
        QualifiedTable(connection.Provider, table);
        return WithConnectionAsync<IReadOnlyList<DatabaseColumnInfo>>(connection,
            (database, token) => ReadColumnsAsync(database, connection, table, token), cancellationToken);
    }

    public Task EnsurePointTableAsync(DatabaseConnectionConfiguration connection, DatabaseTableInfo table, CancellationToken cancellationToken = default)
    {
        QualifiedTable(connection.Provider, table);
        _validatedTables.Clear();
        return WithConnectionAsync(connection, async (database, token) =>
        {
            var columns = await ReadColumnsAsync(database, connection, table, token).ConfigureAwait(false);
            if (columns.Count > 0)
            {
                await ValidatePointTableAsync(database, connection, table, columns, token).ConfigureAwait(false);
                return true; // Existing tables are inspected, never rewritten or altered.
            }
            await using (var command = Command(database, connection, BuildCreatePointTableSql(connection.Provider, table)))
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            // DDL is deliberately outside a transaction: MySQL/Oracle DDL commits implicitly.
            // A partially completed DDL operation is reported; ordinary writes never try to repair schema.
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table.Schema + "." + table.Name)))[..12];
            foreach (var (suffix, fields) in new[] { ("T", new[] { "sample_time" }), ("P", new[] { "tag_id", "sample_time" }) })
            {
                var indexName = QuoteIdentifier(connection.Provider, "EG_" + hash + "_" + suffix);
                if (connection.Provider == ExternalDatabaseProvider.Oracle && table.Schema.Length > 0)
                    indexName = QuoteIdentifier(connection.Provider, table.Schema) + "." + indexName;
                var sql = $"CREATE INDEX {indexName} ON {QualifiedTable(connection.Provider, table)} ({string.Join(", ", fields.Select(field => Q(connection.Provider, field)))})";
                await using var command = Command(database, connection, sql);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken);
    }

    public Task WriteRowsAsync(DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group,
        IReadOnlyList<DatabaseWriteRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(rows);
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count == 0) return Task.CompletedTask;
        if (rows.Count > 10000) throw new ArgumentException("单次数据库写入最多 10000 行，请按配置分批提交。");
        QualifiedTable(connection.Provider, Table(group));
        if (!Enum.IsDefined(group.Mode)) throw new ArgumentException("数据库存储模式无效。");
        var pointRows = group.Mode == DatabaseStorageMode.PointRows;
        var columns = pointRows ? DatabasePointTable.Columns.ToArray() : group.Columns.Select(mapping => mapping.Column).ToArray();
        if (columns.Length is < 1 or > 256 || columns.Distinct(StringComparer.Ordinal).Count() != columns.Length)
            throw new ArgumentException("映射须包含 1–256 个互不重复的字段。");
        foreach (var column in columns) QuoteIdentifier(connection.Provider, pointRows ? PointColumn(connection.Provider, column) : column);
        foreach (var row in rows)
        {
            if (row is null || row.Values is null || row.Values.Count != columns.Length || columns.Any(column => !row.Values.ContainsKey(column)))
                throw new ArgumentException("写入行的字段必须与标准点表或配置映射完整一致。");
            if (pointRows && (string.IsNullOrWhiteSpace(row.EventId) || row.EventId.Length > 64
                || !string.Equals(row.Values["event_id"] as string, row.EventId, StringComparison.Ordinal)))
                throw new ArgumentException("标准点表写入必须保留一致且长度不超过 64 字符的稳定 event_id。");
        }
        var cacheKey = MetadataCacheKey(connection, group);
        return InvalidateOnFailureAsync(cacheKey, () => WithConnectionAsync(connection, async (database, token) =>
        {
            var metadata = await GetValidatedColumnsAsync(database, connection, group, columns, cacheKey, token).ConfigureAwait(false);
            var types = metadata.ToDictionary(column => column.Name, StringComparer.Ordinal);
            await using var transaction = await database.BeginTransactionAsync(token).ConfigureAwait(false);
            try
            {
                if (connection.Provider == ExternalDatabaseProvider.Oracle)
                    await WriteOracleRowsAsync(database, transaction, connection, group, columns, rows, types, token).ConfigureAwait(false);
                // PostgreSQL/MySQL use multi-row INSERTs, bounded below parameter limits.
                var chunkSize = Math.Min(1000, 50_000 / columns.Length);
                for (var offset = 0; connection.Provider != ExternalDatabaseProvider.Oracle && offset < rows.Count; offset += chunkSize)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(chunkSize, rows.Count - offset);
                    await using var command = Command(database, connection, BuildInsertSql(connection.Provider, Table(group), columns, count, pointRows), transaction);
                    for (var rowIndex = 0; rowIndex < count; rowIndex++)
                    for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                    {
                        var column = columns[columnIndex];
                        var databaseColumn = pointRows ? PointColumn(connection.Provider, column) : column;
                        var fieldType = pointRows ? PointFieldType(column) : group.Columns[columnIndex].FieldType;
                        AddValueParameter(command, connection.Provider, $"p{rowIndex}_{columnIndex}", rows[offset + rowIndex].Values[column], fieldType,
                            types.GetValueOrDefault(databaseColumn)?.DataType);
                    }
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(connection.CommandTimeoutSeconds, 5)));
                try { await transaction.RollbackAsync(rollback.Token).ConfigureAwait(false); }
                catch { /* Dispose the connection; preserve the actual write/commit failure. */ }
                throw;
            }
            return true;
        }, cancellationToken));
    }

    private async Task InvalidateOnFailureAsync(string cacheKey, Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch { _validatedTables.TryRemove(cacheKey, out _); throw; }
    }

    private static string MetadataCacheKey(DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group) =>
        JsonSerializer.Serialize(new object[] { connection.Provider, connection.Host.ToLowerInvariant(), connection.Port,
            connection.Database, connection.Username, connection.PasswordEnvironmentVariable, connection.UseTls,
            group.Schema, group.Table, group.Mode, group.Columns.Select(column => new { column.Column, column.FieldType }).ToArray() });

    private async Task<IReadOnlyList<DatabaseColumnInfo>> GetValidatedColumnsAsync(DbConnection database,
        DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group, IReadOnlyList<string> columns,
        string cacheKey, CancellationToken token)
    {
        if (_validatedTables.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > Environment.TickCount64) return cached.Columns;
        var metadata = await ReadColumnsAsync(database, connection, Table(group), token).ConfigureAwait(false);
        if (metadata.Count == 0) throw new ConnectorConfigurationException("目标数据表不存在或当前账户无权访问。请先选择已有表，或显式创建标准点表。");
        if (group.Mode == DatabaseStorageMode.PointRows) await ValidatePointTableAsync(database, connection, Table(group), metadata, token).ConfigureAwait(false);
        else ValidateMappedColumns(columns, metadata);
        if (connection.Provider == ExternalDatabaseProvider.MySql) await RequireTransactionalMySqlTableAsync(database, connection, Table(group), token).ConfigureAwait(false);
        // Bound both lifetime and cardinality. Database errors invalidate immediately, so a changed
        // schema is re-read on retry; normal batches avoid repeated catalog round-trips.
        if (_validatedTables.Count >= 256) _validatedTables.Clear();
        _validatedTables[cacheKey] = new(metadata, Environment.TickCount64 + 30_000);
        return metadata;
    }

    public Task<IReadOnlyList<TelemetrySample>> QueryPointHistoryAsync(DatabaseConnectionConfiguration connection,
        DatabaseWriteGroupConfiguration group, HistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(query);
        if (group.Mode != DatabaseStorageMode.PointRows) throw new ArgumentException("历史趋势仅支持标准点表；自定义业务表请使用其业务查询。");
        if (query.From > query.To) throw new ArgumentException("查询开始时间不能晚于结束时间。");
        QualifiedTable(connection.Provider, Table(group));
        return WithConnectionAsync<IReadOnlyList<TelemetrySample>>(connection, async (database, token) =>
        {
            var provider = connection.Provider;
            var sql = $"SELECT {string.Join(", ", DatabasePointTable.Columns.Select(column => Q(provider, column)))} FROM {QualifiedTable(provider, Table(group))}"
                + $" WHERE {Q(provider, "sample_time")} >= {Parameter(provider, "from_time")} AND {Q(provider, "sample_time")} <= {Parameter(provider, "to_time")}";
            if (query.DeviceId.HasValue) sql += $" AND {Q(provider, "device_id")} = {Parameter(provider, "device")}";
            if (query.TagId.HasValue) sql += $" AND {Q(provider, "tag_id")} = {Parameter(provider, "tag")}";
            sql += $" ORDER BY {Q(provider, "sample_time")} DESC, {Q(provider, "event_id")} DESC";
            sql += provider == ExternalDatabaseProvider.Oracle ? " FETCH FIRST :row_limit ROWS ONLY" : " LIMIT @row_limit";
            await using var command = Command(database, connection, sql);
            var timestampType = provider == ExternalDatabaseProvider.PostgreSql ? "timestamp with time zone" : "TIMESTAMP";
            AddValueParameter(command, provider, "from_time", query.From, DatabaseFieldType.Timestamp, timestampType);
            AddValueParameter(command, provider, "to_time", query.To, DatabaseFieldType.Timestamp, timestampType);
            if (query.DeviceId.HasValue) AddValueParameter(command, provider, "device", query.DeviceId.Value.ToString("D"));
            if (query.TagId.HasValue) AddValueParameter(command, provider, "tag", query.TagId.Value.ToString("D"));
            AddValueParameter(command, provider, "row_limit", Math.Clamp(query.Limit, 1, 10000));
            var result = new List<TelemetrySample>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(2), out var deviceId) || !Guid.TryParse(reader.GetString(4), out var tagId)
                    || !Enum.TryParse<SampleQuality>(reader.GetString(10), out var quality) || !Enum.IsDefined(quality))
                    throw new ConnectorConfigurationException("标准点表包含无效的设备标识、点位标识或质量码，请核对数据结构。");
                var utc = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                result.Add(new(deviceId, Text(reader, 3), tagId, Text(reader, 5), Text(reader, 6),
                    JsonValue(reader, 7), JsonValue(reader, 8), quality, new DateTimeOffset(utc), Text(reader, 11), reader.IsDBNull(12) ? null : reader.GetString(12)));
            }
            return result;
        }, cancellationToken);
    }

    private async Task<T> WithConnectionAsync<T>(DatabaseConnectionConfiguration configuration,
        Func<DbConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ValidateConnection(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        using var openDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await using var database = _connectionFactory(configuration);
            openDeadline.CancelAfter(TimeSpan.FromSeconds(configuration.ConnectionTimeoutSeconds));
            await database.OpenAsync(openDeadline.Token).ConfigureAwait(false);
            openDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            commandDeadline.CancelAfter(TimeSpan.FromSeconds(configuration.CommandTimeoutSeconds));
            if (configuration.Provider == ExternalDatabaseProvider.MySql)
            {
                // Reject truncation/coercion even when the server's default SQL mode is lax.
                await using var utc = Command(database, configuration, "SET SESSION time_zone = '+00:00', sql_mode = CONCAT_WS(',', @@SESSION.sql_mode, 'STRICT_ALL_TABLES')");
                await utc.ExecuteNonQueryAsync(commandDeadline.Token).ConfigureAwait(false);
            }
            else if (configuration.Provider == ExternalDatabaseProvider.Oracle)
            {
                await using var utc = Command(database, configuration, "ALTER SESSION SET TIME_ZONE = '+00:00'");
                await utc.ExecuteNonQueryAsync(commandDeadline.Token).ConfigureAwait(false);
            }
            return await action(database, commandDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (ConnectorConfigurationException) { throw; }
        catch (Exception exception)
        {
            if (openDeadline.IsCancellationRequested || commandDeadline.IsCancellationRequested || exception is TimeoutException or OperationCanceledException)
                throw new TimeoutException("数据库操作超时；未收到成功确认的标准点表批次将按原 event_id 重试。请检查网络、数据库负载和超时设置。");
            // Provider messages can contain credentials, SQL, payloads or server diagnostics.
            // Return only a stable provider/error code; never attach the original exception as InnerException.
            throw new DatabaseWriteException(SafeError(configuration.Provider, exception), IsTransientFailure(exception));
        }
    }

    private static DbCommand Command(DbConnection database, DatabaseConnectionConfiguration configuration, string sql, DbTransaction? transaction = null)
    {
        var command = database.CreateCommand();
        command.CommandTimeout = configuration.CommandTimeoutSeconds;
        command.CommandText = sql;
        command.Transaction = transaction;
        if (command is OracleCommand oracle) oracle.BindByName = true;
        return command;
    }

    internal static string SafeError(ExternalDatabaseProvider provider, Exception exception)
    {
        var code = exception switch
        {
            MySqlException mysql => mysql.Number.ToString(CultureInfo.InvariantCulture),
            PostgresException postgres => postgres.SqlState,
            OracleException oracle => "ORA-" + Math.Abs(oracle.Number).ToString("D5", CultureInfo.InvariantCulture),
            DbException => "DB", _ => "CONNECTION"
        };
        return $"数据库操作失败（{provider}，错误代码 {code}）。请检查连接、账户权限、目标表结构、字段类型和数据库服务端日志；连接凭据及写入数据未写入此错误。";
    }

    private static async Task<IReadOnlyList<DatabaseColumnInfo>> ReadColumnsAsync(DbConnection database,
        DatabaseConnectionConfiguration configuration, DatabaseTableInfo table, CancellationToken token)
    {
        QualifiedTable(configuration.Provider, table);
        var schemaExpression = configuration.Provider switch
        {
            ExternalDatabaseProvider.MySql => "COALESCE(NULLIF(@eg_schema, ''), DATABASE())",
            ExternalDatabaseProvider.PostgreSql => "COALESCE(NULLIF(@eg_schema, ''), current_schema())",
            _ => "NVL(:eg_schema, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))"
        };
        var sql = configuration.Provider switch
        {
            ExternalDatabaseProvider.MySql => $"SELECT column_name, data_type, is_nullable, CASE WHEN column_default IS NULL THEN 'N' ELSE 'Y' END, extra, generation_expression FROM information_schema.columns WHERE table_schema = {schemaExpression} AND table_name = @eg_table_name ORDER BY ordinal_position",
            ExternalDatabaseProvider.PostgreSql => $"SELECT column_name, data_type, is_nullable, CASE WHEN column_default IS NULL THEN 'N' ELSE 'Y' END, is_identity, is_generated FROM information_schema.columns WHERE table_schema = {schemaExpression} AND table_name = @eg_table_name ORDER BY ordinal_position",
            _ => $"SELECT column_name, data_type, nullable, CASE WHEN default_length > 0 THEN 'Y' ELSE 'N' END, identity_column, virtual_column FROM all_tab_cols WHERE owner = {schemaExpression} AND table_name = :eg_table_name AND hidden_column = 'NO' ORDER BY column_id"
        };
        await using var command = Command(database, configuration, sql);
        AddValueParameter(command, configuration.Provider, "eg_schema", string.IsNullOrEmpty(table.Schema) ? null : table.Schema, DatabaseFieldType.Text);
        AddValueParameter(command, configuration.Provider, "eg_table_name", table.Name);
        var columns = new List<DatabaseColumnInfo>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var nullable = Text(reader, 2);
            var extra = Text(reader, 4);
            var generated = Text(reader, 5);
            var isGenerated = configuration.Provider switch
            {
                ExternalDatabaseProvider.MySql => extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) || extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) && !extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase) || generated.Length > 0,
                ExternalDatabaseProvider.PostgreSql => extra == "YES" || generated != "NEVER",
                _ => extra == "YES" || generated == "YES"
            };
            columns.Add(new(reader.GetString(0), reader.GetString(1), nullable is "YES" or "Y", Text(reader, 3) == "Y", isGenerated));
        }
        return columns;
    }

    private static void ValidateMappedColumns(IReadOnlyList<string> columns, IReadOnlyList<DatabaseColumnInfo> metadata)
    {
        var mapped = columns.ToHashSet(StringComparer.Ordinal);
        var available = metadata.ToDictionary(column => column.Name, StringComparer.Ordinal);
        if (columns.Any(column => !available.ContainsKey(column))) throw new ConnectorConfigurationException("映射字段在目标表中不存在；请重新读取表字段，Oracle/PostgreSQL 字段大小写须与元数据一致。");
        if (columns.Any(column => available[column].IsGenerated)) throw new ConnectorConfigurationException("自动生成或标识字段不能作为显式写入目标，请移除对应映射。");
        if (metadata.Any(column => !mapped.Contains(column.Name) && !column.IsNullable && !column.HasDefault && !column.IsGenerated))
            throw new ConnectorConfigurationException("目标表有未映射且没有默认值的必填字段，请补全映射。");
    }

    private static async Task ValidatePointTableAsync(DbConnection database, DatabaseConnectionConfiguration configuration,
        DatabaseTableInfo table, IReadOnlyList<DatabaseColumnInfo> metadata, CancellationToken token)
    {
        var expected = DatabasePointTable.Columns.Select(column => PointColumn(configuration.Provider, column)).ToArray();
        ValidateMappedColumns(expected, metadata);
        var schema = configuration.Provider switch
        {
            ExternalDatabaseProvider.MySql => "COALESCE(NULLIF(@eg_schema, ''), DATABASE())",
            ExternalDatabaseProvider.PostgreSql => "COALESCE(NULLIF(@eg_schema, ''), current_schema())",
            _ => "NVL(:eg_schema, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))"
        };
        var sql = configuration.Provider switch
        {
            ExternalDatabaseProvider.MySql => $"SELECT index_name, column_name FROM information_schema.statistics WHERE table_schema = {schema} AND table_name = @eg_table_name AND non_unique = 0 ORDER BY index_name, seq_in_index",
            ExternalDatabaseProvider.PostgreSql => $"SELECT tc.constraint_name, kcu.column_name FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.constraint_catalog = kcu.constraint_catalog AND tc.constraint_schema = kcu.constraint_schema AND tc.constraint_name = kcu.constraint_name AND tc.table_name = kcu.table_name WHERE tc.table_schema = {schema} AND tc.table_name = @eg_table_name AND tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE') ORDER BY tc.constraint_name, kcu.ordinal_position",
            _ => $"SELECT c.constraint_name, k.column_name FROM all_constraints c JOIN all_cons_columns k ON c.owner = k.owner AND c.constraint_name = k.constraint_name AND c.table_name = k.table_name WHERE c.owner = {schema} AND c.table_name = :eg_table_name AND c.constraint_type IN ('P', 'U') AND c.status = 'ENABLED' ORDER BY c.constraint_name, k.position"
        };
        await using var command = Command(database, configuration, sql);
        AddValueParameter(command, configuration.Provider, "eg_schema", table.Schema.Length == 0 ? null : table.Schema, DatabaseFieldType.Text);
        AddValueParameter(command, configuration.Provider, "eg_table_name", table.Name);
        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                if (!keys.TryGetValue(name, out var fields)) keys[name] = fields = [];
                fields.Add(Text(reader, 1));
            }
        }
        var eventColumn = PointColumn(configuration.Provider, "event_id");
        if (metadata.Single(column => column.Name == eventColumn).IsNullable)
            throw new ConnectorConfigurationException("标准点表 event_id 必须为 NOT NULL，才能可靠验证重试与键冲突。");
        if (!keys.Values.Any(fields => fields.Count == 1 && fields[0] == eventColumn))
            throw new ConnectorConfigurationException("标准点表必须具有仅包含 event_id 的主键或唯一约束，才能保证补偿重试不重复写入。");
        if (configuration.Provider == ExternalDatabaseProvider.MySql && keys.Values.Any(fields => fields.Count != 1 || fields[0] != eventColumn))
            throw new ConnectorConfigurationException("MySQL 标准点表不能包含 event_id 以外的唯一约束，以免把业务键冲突误判为补偿重试。");
        var timestamp = metadata.Single(column => column.Name == PointColumn(configuration.Provider, "sample_time")).DataType;
        if (configuration.Provider == ExternalDatabaseProvider.PostgreSql && !string.Equals(timestamp, "timestamp with time zone", StringComparison.OrdinalIgnoreCase)
            || configuration.Provider == ExternalDatabaseProvider.MySql && !string.Equals(timestamp, "datetime", StringComparison.OrdinalIgnoreCase)
            || configuration.Provider == ExternalDatabaseProvider.Oracle && (!timestamp.StartsWith("TIMESTAMP", StringComparison.OrdinalIgnoreCase) || timestamp.Contains("TIME ZONE", StringComparison.OrdinalIgnoreCase)))
            throw new ConnectorConfigurationException("标准点表 sample_time 类型不匹配；须使用 PostgreSQL timestamptz、MySQL datetime 或 Oracle UTC timestamp。");
    }

    private static async Task RequireTransactionalMySqlTableAsync(DbConnection database, DatabaseConnectionConfiguration configuration, DatabaseTableInfo table, CancellationToken token)
    {
        await using var command = Command(database, configuration, "SELECT engine FROM information_schema.tables WHERE table_schema = COALESCE(NULLIF(@eg_schema, ''), DATABASE()) AND table_name = @eg_table_name");
        AddValueParameter(command, configuration.Provider, "eg_schema", table.Schema.Length == 0 ? null : table.Schema, DatabaseFieldType.Text);
        AddValueParameter(command, configuration.Provider, "eg_table_name", table.Name);
        var engine = Convert.ToString(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (!string.Equals(engine, "InnoDB", StringComparison.OrdinalIgnoreCase))
            throw new ConnectorConfigurationException("MySQL 采集写入要求目标表使用 InnoDB，以保证整批提交和失败回滚。");
    }

    private static async Task<bool> EventExistsAsync(DbConnection database, DbTransaction transaction,
        DatabaseConnectionConfiguration configuration, DatabaseTableInfo table, string eventId, CancellationToken token)
    {
        await using var command = Command(database, configuration,
            $"SELECT {Q(configuration.Provider, "event_id")} FROM {QualifiedTable(configuration.Provider, table)} WHERE {Q(configuration.Provider, "event_id")} = {Parameter(configuration.Provider, "event")}", transaction);
        AddValueParameter(command, configuration.Provider, "event", eventId);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null and not DBNull;
    }

    private static DatabaseFieldType PointFieldType(string column) => column switch
    { "sample_time" => DatabaseFieldType.Timestamp, "numeric_value" => DatabaseFieldType.Double, _ => DatabaseFieldType.Text };
    private static string Text(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    private static object? JsonValue(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        try
        {
            using var document = JsonDocument.Parse(reader.GetString(ordinal));
            var element = document.RootElement.Clone();
            return element.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? element : NormalizeValue(element);
        }
        catch (JsonException) { throw new ConnectorConfigurationException("标准点表 raw_value/value_text 包含无效 JSON，无法还原采样类型。"); }
    }
    private sealed class ConnectorConfigurationException(string message) : DatabaseWriteException(message, false);
}
