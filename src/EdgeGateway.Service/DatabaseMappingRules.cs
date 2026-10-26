using EdgeGateway.Model;

namespace EdgeGateway.Service;

/// <summary>Shared save-time checks; constants use the same conversion as runtime rows.</summary>
public static class DatabaseMappingRules
{
    public static DatabaseFieldType InferFieldType(string databaseType)
    {
        var type = databaseType.Trim().ToLowerInvariant();
        var parenthesis = type.IndexOf('(');
        if (parenthesis >= 0) type = type[..parenthesis].Trim();
        if (type.EndsWith(" unsigned", StringComparison.Ordinal)) type = type[..^9];
        return type switch
        {
            "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "int2" or "int4" or "int8" => DatabaseFieldType.Int64,
            "decimal" or "numeric" or "number" => DatabaseFieldType.Decimal,
            "float" or "double" or "double precision" or "real" or "binary_float" or "binary_double" => DatabaseFieldType.Double,
            "bool" or "boolean" => DatabaseFieldType.Boolean,
            "date" or "datetime" or "timestamp" or "timestamp without time zone" or "timestamp with time zone" or "timestamp with local time zone" => DatabaseFieldType.Timestamp,
            "char" or "varchar" or "varchar2" or "nvarchar2" or "nchar" or "text" or "tinytext" or "mediumtext" or "longtext"
                or "character" or "character varying" or "clob" or "nclob" => DatabaseFieldType.Text,
            _ => DatabaseFieldType.Auto
        };
    }

    public static string? ValidateValue(DatabaseColumnMapping mapping)
    {
        if (mapping.Source == DatabaseValueSource.Quality && mapping.FieldType is not (DatabaseFieldType.Auto or DatabaseFieldType.Text))
            return $"字段 [{mapping.Column}] 的 quality [质量] 来源是 Good / Bad / Uncertain 文本，不能转换为 {mapping.FieldType}；请使用文本类型，或改为手动常量并填写业务状态值。";
        if (mapping.Source != DatabaseValueSource.Constant) return null;
        try { _ = DatabaseRowMapper.ConvertField(mapping.ConstantValue, mapping.FieldType); return null; }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException or ArgumentException)
        { return $"字段 [{mapping.Column}] 的常量不符合所选类型 {mapping.FieldType}：{exception.Message}"; }
    }
}
