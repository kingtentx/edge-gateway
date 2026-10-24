using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdgeGateway.Model;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace EdgeGateway.DataAccess;

public sealed partial class ExternalDatabaseConnector
{
    // Identifiers are never concatenated until validated. Each part is quoted independently;
    // schema and table are separate fields, so dots, comments and quoted SQL cannot be supplied.
    internal static string QuoteIdentifier(ExternalDatabaseProvider provider, string identifier)
    {
        // The shared configuration contract intentionally uses the portable 63-byte subset.
        if (string.IsNullOrWhiteSpace(identifier) || Encoding.UTF8.GetByteCount(identifier) > 63
            || !Regex.IsMatch(identifier, @"\A[\p{L}_][\p{L}\p{N}_]*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("表名、Schema 和字段名仅允许字母、数字、下划线，须以字母或下划线开头，且 UTF-8 编码不能超过 63 字节；超长名称不自动截断。");
        return provider == ExternalDatabaseProvider.MySql ? "`" + identifier + "`" : "\"" + identifier + "\"";
    }

    internal static string QualifiedTable(ExternalDatabaseProvider provider, DatabaseTableInfo table) =>
        string.IsNullOrEmpty(table.Schema) ? QuoteIdentifier(provider, table.Name)
            : QuoteIdentifier(provider, table.Schema) + "." + QuoteIdentifier(provider, table.Name);

    private static string PointColumn(ExternalDatabaseProvider provider, string column) =>
        provider == ExternalDatabaseProvider.Oracle ? column.ToUpperInvariant() : column;
    private static string Q(ExternalDatabaseProvider provider, string column) => QuoteIdentifier(provider, PointColumn(provider, column));
    private static string Parameter(ExternalDatabaseProvider provider, string name) => (provider == ExternalDatabaseProvider.Oracle ? ":" : "@") + name;
    private static DatabaseTableInfo Table(DatabaseWriteGroupConfiguration group) => new(group.Schema, group.Table);

    internal static void ValidateConnection(DatabaseConnectionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Enum.IsDefined(configuration.Provider)) throw new ArgumentException("不支持的数据库类型。");
        var host = configuration.Host?.Trim() ?? "";
        if (host.Length == 0 || host.Length > 253 || (!IPAddress.TryParse(host.Trim('[', ']'), out _)
            && !Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.-]*$", RegexOptions.CultureInvariant)))
            throw new ArgumentException("数据库主机必须是一个 IP 地址或域名，不能填写连接串或协议前缀。");
        if (configuration.Port is < 1 or > 65535) throw new ArgumentException("数据库端口须为 1–65535。");
        if (string.IsNullOrWhiteSpace(configuration.Database) || configuration.Database.Length > 128 || configuration.Database.Any(char.IsControl))
            throw new ArgumentException("数据库名称或 Oracle 服务名不能为空、包含控制字符或超过 128 字符。");
        if (configuration.Provider == ExternalDatabaseProvider.Oracle && !Regex.IsMatch(configuration.Database, @"\A[A-Za-z0-9_.-]{1,128}\z"))
            throw new ArgumentException("Oracle 服务名仅支持字母、数字、下划线、点与短横线；请勿填写 TNS 描述符。");
        if (string.IsNullOrWhiteSpace(configuration.Username) || configuration.Username.Length > 128 || configuration.Username.Any(char.IsControl))
            throw new ArgumentException("数据库用户名不能为空、包含控制字符或超过 128 字符。");
        if (configuration.Password is null) throw new ArgumentException("数据库密码不能为 null；无密码时请留空。");
        if (configuration.ConnectionTimeoutSeconds is < 1 or > 120 || configuration.CommandTimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("连接超时须为 1–120 秒，命令超时须为 1–300 秒。");
    }

    internal static string BuildConnectionString(DatabaseConnectionConfiguration configuration)
    {
        ValidateConnection(configuration);
        var password = configuration.Password;
        return configuration.Provider switch
        {
            ExternalDatabaseProvider.MySql => new MySqlConnectionStringBuilder
            {
                Server = configuration.Host.Trim().Trim('[', ']'), Port = (uint)configuration.Port,
                Database = configuration.Database, UserID = configuration.Username, Password = password,
                ConnectionTimeout = (uint)configuration.ConnectionTimeoutSeconds, DefaultCommandTimeout = (uint)configuration.CommandTimeoutSeconds,
                SslMode = configuration.UseTls ? MySqlSslMode.VerifyFull : MySqlSslMode.Disabled,
                DateTimeKind = MySqlDateTimeKind.Utc, Pooling = true, MinimumPoolSize = 0, MaximumPoolSize = 20,
                PersistSecurityInfo = false, AllowLoadLocalInfile = false, AllowPublicKeyRetrieval = false
            }.ConnectionString,
            ExternalDatabaseProvider.PostgreSql => new NpgsqlConnectionStringBuilder
            {
                Host = configuration.Host.Trim().Trim('[', ']'), Port = configuration.Port, Database = configuration.Database,
                Username = configuration.Username, Password = password, Timeout = configuration.ConnectionTimeoutSeconds,
                CommandTimeout = configuration.CommandTimeoutSeconds, SslMode = configuration.UseTls ? SslMode.VerifyFull : SslMode.Disable,
                Pooling = true, MinPoolSize = 0, MaxPoolSize = 20, IncludeErrorDetail = false, PersistSecurityInfo = false,
                Timezone = "UTC", ApplicationName = "EdgeGateway"
            }.ConnectionString,
            ExternalDatabaseProvider.Oracle => new OracleConnectionStringBuilder
            {
                UserID = configuration.Username, Password = password, ConnectionTimeout = configuration.ConnectionTimeoutSeconds,
                Pooling = true, MinPoolSize = 0, MaxPoolSize = 20, PersistSecurityInfo = false,
                // Managed ODP.NET is used: no separately installed Oracle client is required.
                DataSource = $"(DESCRIPTION=(CONNECT_TIMEOUT={configuration.ConnectionTimeoutSeconds})(TRANSPORT_CONNECT_TIMEOUT={configuration.ConnectionTimeoutSeconds})(RETRY_COUNT=0)"
                    + $"(ADDRESS=(PROTOCOL={(configuration.UseTls ? "TCPS" : "TCP")})(HOST={configuration.Host.Trim().Trim('[', ']')})(PORT={configuration.Port}))"
                    + $"(CONNECT_DATA=(SERVICE_NAME={configuration.Database}))"
                    + (configuration.UseTls ? "(SECURITY=(SSL_SERVER_DN_MATCH=YES))" : "") + ")"
            }.ConnectionString,
            _ => throw new ArgumentException("不支持的数据库类型。")
        };
    }

    private static DbConnection CreateConnection(DatabaseConnectionConfiguration configuration)
    {
        var connectionString = BuildConnectionString(configuration);
        return configuration.Provider switch
        {
            ExternalDatabaseProvider.MySql => new MySqlConnection(connectionString),
            ExternalDatabaseProvider.PostgreSql => new NpgsqlConnection(connectionString),
            ExternalDatabaseProvider.Oracle => new OracleConnection(connectionString),
            _ => throw new ArgumentException("不支持的数据库类型。")
        };
    }

    internal static string BuildCreatePointTableSql(ExternalDatabaseProvider provider, DatabaseTableInfo table)
    {
        var name = QualifiedTable(provider, table);
        string Column(string column, string type) => Q(provider, column) + " " + type;
        var text = provider switch { ExternalDatabaseProvider.MySql => "LONGTEXT", ExternalDatabaseProvider.Oracle => "NCLOB", _ => "text" };
        var small = provider == ExternalDatabaseProvider.Oracle ? "VARCHAR2" : "varchar";
        var timestamp = provider switch { ExternalDatabaseProvider.MySql => "datetime(6)", ExternalDatabaseProvider.Oracle => "timestamp(6)", _ => "timestamp with time zone" };
        var number = provider switch { ExternalDatabaseProvider.MySql => "double", ExternalDatabaseProvider.Oracle => "BINARY_DOUBLE", _ => "double precision" };
        var columns = new[]
        {
            Column("event_id", small + "(64) NOT NULL PRIMARY KEY"), Column("sample_time", timestamp + " NOT NULL"),
            Column("device_id", small + "(36) NOT NULL"), Column("device_name", text), Column("tag_id", small + "(36) NOT NULL"),
            Column("tag_name", text), Column("address", text), Column("raw_value", text), Column("value_text", text),
            Column("numeric_value", number), Column("quality", small + "(16) NOT NULL"), Column("unit", text), Column("error", text)
        };
        return $"CREATE TABLE {name} ({string.Join(", ", columns)})"
            + (provider == ExternalDatabaseProvider.MySql ? " ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin" : "");
    }

    internal static string BuildInsertSql(ExternalDatabaseProvider provider, DatabaseTableInfo table,
        IReadOnlyList<string> columns, int rowCount, bool pointRows)
    {
        if (rowCount < 1 || rowCount > 1000 || columns.Count is < 1 or > 256) throw new ArgumentException("数据库批量写入行数或列数超出限制。");
        var names = columns.Select(column => QuoteIdentifier(provider, pointRows ? PointColumn(provider, column) : column)).ToArray();
        var values = Enumerable.Range(0, rowCount).Select(row => "(" + string.Join(", ", Enumerable.Range(0, columns.Count)
            .Select(column => Parameter(provider, $"p{row}_{column}"))) + ")");
        var sql = $"INSERT INTO {QualifiedTable(provider, table)} ({string.Join(", ", names)}) VALUES {string.Join(", ", values)}";
        if (pointRows && provider == ExternalDatabaseProvider.PostgreSql) sql += $" ON CONFLICT ({Q(provider, "event_id")}) DO NOTHING";
        if (pointRows && provider == ExternalDatabaseProvider.MySql)
        {
            // MySQL 8.0.19+ row aliases avoid deprecated VALUES(column) syntax. Strict mode and
            // NOT NULL event_id turn any other unique-key collision into an error, including a
            // unique index added while validated metadata is cached. An event replay is a no-op.
            var alias = QuoteIdentifier(provider, table.Name == "eg_incoming" ? "eg_incoming_row" : "eg_incoming");
            var key = Q(provider, "event_id");
            var existingKey = QualifiedTable(provider, table) + "." + key;
            sql += $" AS {alias} ON DUPLICATE KEY UPDATE {key} = IF({existingKey} = {alias}.{key}, {existingKey}, NULL)";
        }
        return sql;
    }

    internal static DbParameter AddValueParameter(DbCommand command, ExternalDatabaseProvider provider, string name,
        object? input, DatabaseFieldType requestedType = DatabaseFieldType.Auto, string? databaseType = null)
    {
        var value = NormalizeValue(input);
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        if (command is OracleCommand oracleCommand) oracleCommand.BindByName = true;
        switch (value)
        {
            case DateTimeOffset instant:
                BindTimestamp(parameter, provider, instant.UtcDateTime, databaseType);
                break;
            case DateTime date:
                var utc = date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc);
                BindTimestamp(parameter, provider, utc, databaseType);
                break;
            case bool boolean when parameter is OracleParameter oracle:
                oracle.OracleDbType = string.Equals(databaseType, "BOOLEAN", StringComparison.OrdinalIgnoreCase) ? OracleDbType.Boolean : OracleDbType.Int16;
                oracle.Value = oracle.OracleDbType == OracleDbType.Boolean ? boolean : (object)(short)(boolean ? 1 : 0);
                break;
            case string text:
                parameter.DbType = DbType.String;
                if (parameter is OracleParameter oracleText)
                    oracleText.OracleDbType = databaseType?.Contains("CLOB", StringComparison.OrdinalIgnoreCase) == true || text.Length > 2000
                        ? OracleDbType.NClob : OracleDbType.NVarchar2;
                parameter.Value = text;
                break;
            case null:
                parameter.Value = DBNull.Value;
                SetNullType(parameter, provider, requestedType, databaseType);
                break;
            case ulong unsigned:
                parameter.DbType = DbType.Decimal;
                parameter.Value = (decimal)unsigned;
                break;
            case uint or ushort or byte or sbyte when parameter is NpgsqlParameter or OracleParameter:
                parameter.DbType = DbType.Int64;
                parameter.Value = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                break;
            case float single when !float.IsFinite(single):
            case double number when !double.IsFinite(number):
                throw new ArgumentException("数据库数值不允许 NaN 或 Infinity。");
            default:
                parameter.Value = value;
                parameter.DbType = value switch
                {
                    bool => DbType.Boolean, byte => DbType.Byte, sbyte => DbType.SByte, short => DbType.Int16, ushort => DbType.UInt16,
                    int => DbType.Int32, uint => DbType.UInt32, long => DbType.Int64, float => DbType.Single,
                    double => DbType.Double, decimal => DbType.Decimal, byte[] => DbType.Binary,
                    _ => throw new ArgumentException("数据库写入值的数据类型不受支持；复杂值请映射为文本。")
                };
                if (parameter is NpgsqlParameter postgres && value is uint or ushort or byte or sbyte)
                { postgres.NpgsqlDbType = NpgsqlDbType.Bigint; postgres.Value = Convert.ToInt64(value, CultureInfo.InvariantCulture); }
                break;
        }
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static object? NormalizeValue(object? value) => value switch
    {
        DBNull => null,
        Guid id => id.ToString("D"),
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null, JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => element.GetDouble(), _ => element.GetRawText()
        },
        _ => value
    };

    private static void BindTimestamp(DbParameter parameter, ExternalDatabaseProvider provider, DateTime utc, string? databaseType)
    {
        if (parameter is NpgsqlParameter postgres)
        {
            var withoutZone = string.Equals(databaseType, "timestamp without time zone", StringComparison.OrdinalIgnoreCase)
                || string.Equals(databaseType, "timestamp", StringComparison.OrdinalIgnoreCase);
            postgres.NpgsqlDbType = withoutZone ? NpgsqlDbType.Timestamp : NpgsqlDbType.TimestampTz;
            postgres.Value = withoutZone ? DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) : utc;
        }
        else if (parameter is OracleParameter oracle)
        {
            if (databaseType?.Contains("WITH TIME ZONE", StringComparison.OrdinalIgnoreCase) == true)
            { oracle.OracleDbType = OracleDbType.TimeStampTZ; oracle.Value = new OracleTimeStampTZ(utc, "+00:00"); }
            else { oracle.OracleDbType = string.Equals(databaseType, "DATE", StringComparison.OrdinalIgnoreCase) ? OracleDbType.Date : OracleDbType.TimeStamp; oracle.Value = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified); }
        }
        else { parameter.DbType = DbType.DateTime; parameter.Value = utc; }
    }

    private static void SetNullType(DbParameter parameter, ExternalDatabaseProvider provider, DatabaseFieldType type, string? databaseType)
    {
        if (type == DatabaseFieldType.Timestamp) { BindTimestamp(parameter, provider, DateTime.UnixEpoch, databaseType); parameter.Value = DBNull.Value; return; }
        parameter.DbType = type switch
        {
            DatabaseFieldType.Int64 => DbType.Int64, DatabaseFieldType.Decimal => DbType.Decimal,
            DatabaseFieldType.Double => DbType.Double, DatabaseFieldType.Boolean when provider != ExternalDatabaseProvider.Oracle => DbType.Boolean,
            DatabaseFieldType.Boolean => DbType.Int16, _ => DbType.String
        };
        if (parameter is NpgsqlParameter postgres && type == DatabaseFieldType.Auto) postgres.NpgsqlDbType = NpgsqlDbType.Unknown;
        if (parameter is OracleParameter oracle && databaseType?.Contains("CLOB", StringComparison.OrdinalIgnoreCase) == true) oracle.OracleDbType = OracleDbType.NClob;
        if (parameter is OracleParameter oracleBoolean && type == DatabaseFieldType.Boolean && string.Equals(databaseType, "BOOLEAN", StringComparison.OrdinalIgnoreCase)) oracleBoolean.OracleDbType = OracleDbType.Boolean;
    }
}
