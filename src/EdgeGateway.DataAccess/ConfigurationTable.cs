using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.DataAccess;

/// <summary>Explicit, bounded column map for configuration records; never persists object blobs.</summary>
internal sealed class ConfigurationTable<T>(string name, string keys, string constraints, params string[] properties) where T : new()
{
    private readonly PropertyInfo[] _properties = properties.Select(property => typeof(T).GetProperty(property)
        ?? throw new InvalidOperationException($"Unknown configuration property: {property}")).ToArray();
    private readonly string[] _columns = properties.Select(SnakeCase).ToArray();
    internal string Name => name;

    internal string CreateSql => $"CREATE TABLE {name} ({keys}, " +
        string.Join(", ", _properties.Select((property, index) => ColumnDefinition(property, _columns[index]))) +
        $", {constraints}) STRICT;";

    internal object[] Capture(T value) => _properties.Select(property => Encode(property.GetValue(value), property.PropertyType)).ToArray();

    internal void Insert(SqliteConnection connection, SqliteTransaction transaction, string slot,
        object[] values, params (string Name, object Value)[] keysAndValues)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = new[] { "slot" }.Concat(keysAndValues.Select(key => key.Name)).Concat(_columns).ToArray();
        var arguments = new object[] { slot }.Concat(keysAndValues.Select(key => key.Value)).Concat(values).ToArray();
        command.CommandText = $"INSERT INTO {name} ({string.Join(',', names.Select(Quote))}) VALUES ({string.Join(',', names.Select((_, i) => "$p" + i))});";
        for (var i = 0; i < arguments.Length; i++) command.Parameters.AddWithValue("$p" + i, arguments[i]);
        command.ExecuteNonQuery();
    }

    internal List<T> Read(SqliteConnection connection, SqliteTransaction transaction, string slot,
        string orderBy = "slot", (string Name, object Value)? parent = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(',', _columns.Select(Quote))} FROM {name} WHERE slot=$slot" +
            (parent is { } key ? $" AND {Quote(key.Name)}=$parent" : "") + $" ORDER BY {orderBy}";
        command.Parameters.AddWithValue("$slot", slot);
        if (parent is { } filter) command.Parameters.AddWithValue("$parent", filter.Value);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            var item = new T();
            Apply(reader, item);
            result.Add(item);
        }
        return result;
    }

    internal void ReadRequired(SqliteConnection connection, SqliteTransaction transaction, string slot, T target,
        (string Name, object Value)? parent = null)
    {
        var items = Read(connection, transaction, slot, parent: parent);
        if (items.Count != 1) throw new InvalidDataException($"配置表 {name} 缺少唯一的必需记录。");
        foreach (var property in _properties) property.SetValue(target, property.GetValue(items[0]));
    }

    private void Apply(SqliteDataReader reader, T target)
    {
        for (var i = 0; i < _properties.Length; i++)
        {
            var property = _properties[i];
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value;
            if (reader.IsDBNull(i))
            {
                if (Nullable.GetUnderlyingType(property.PropertyType) is null)
                    throw new InvalidDataException($"配置表 {name}.{_columns[i]} 不能为 NULL。");
                value = null;
            }
            else if (type.IsEnum)
            {
                var text = reader.GetString(i);
                value = Enum.Parse(type, text, ignoreCase: false);
                if (!Enum.IsDefined(type, value) || value.ToString() != text)
                    throw new InvalidDataException($"配置表 {name}.{_columns[i]} 的枚举值无效。");
            }
            else if (type == typeof(Guid)) value = Guid.ParseExact(reader.GetString(i), "D");
            else if (type == typeof(bool)) value = reader.GetInt64(i) switch
            {
                0 => false, 1 => true, _ => throw new InvalidDataException($"配置表 {name}.{_columns[i]} 的布尔值无效。")
            };
            else if (type == typeof(int)) value = reader.GetInt32(i);
            else if (type == typeof(double))
            {
                var number = reader.GetDouble(i);
                if (!double.IsFinite(number)) throw new InvalidDataException($"配置表 {name}.{_columns[i]} 包含非有限数值。");
                value = number;
            }
            else value = reader.GetString(i);
            property.SetValue(target, value);
        }
    }

    private static object Encode(object? value, Type propertyType)
    {
        if (value is null)
        {
            if (Nullable.GetUnderlyingType(propertyType) is not null) return DBNull.Value;
            throw new ArgumentException("配置字段不能为空。");
        }
        if (value is double number && !double.IsFinite(number)) throw new ArgumentException("配置不能包含非有限数值。");
        if (value is Enum enumeration)
        {
            if (!Enum.IsDefined(enumeration.GetType(), enumeration)) throw new ArgumentException("配置枚举值无效。");
            return enumeration.ToString();
        }
        return value switch { Guid id => id.ToString("D"), bool flag => flag ? 1 : 0, _ => value };
    }

    private static string ColumnDefinition(PropertyInfo property, string column)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var sqlType = type == typeof(int) || type == typeof(bool) ? "INTEGER" : type == typeof(double) ? "REAL" : "TEXT";
        var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null;
        var check = type == typeof(bool) ? $" CHECK({Quote(column)} IN (0,1))" : type.IsEnum
            ? $" CHECK({Quote(column)} IN ({string.Join(',', Enum.GetNames(type).Select(value => "'" + value + "'"))}))" : "";
        return $"{Quote(column)} {sqlType}{(nullable ? "" : " NOT NULL")}{check}";
    }

    private static string SnakeCase(string value) => Regex.Replace(value, "(?<=[a-z0-9])([A-Z])", "_$1").ToLower(CultureInfo.InvariantCulture);
    private static string Quote(string name) => "\"" + name + "\""; // All identifiers are private constants/property names.
}
