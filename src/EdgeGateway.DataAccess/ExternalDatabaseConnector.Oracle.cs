using System.Data.Common;
using EdgeGateway.Model;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace EdgeGateway.DataAccess;

public sealed partial class ExternalDatabaseConnector
{
    private async Task WriteOracleRowsAsync(DbConnection database, DbTransaction transaction,
        DatabaseConnectionConfiguration connection, DatabaseWriteGroupConfiguration group,
        IReadOnlyList<string> columns, IReadOnlyList<DatabaseWriteRow> rows,
        IReadOnlyDictionary<string, DatabaseColumnInfo> metadata, CancellationToken token)
    {
        var pointRows = group.Mode == DatabaseStorageMode.PointRows;
        var sql = BuildInsertSql(ExternalDatabaseProvider.Oracle, Table(group), columns, 1, pointRows);
        for (var offset = 0; offset < rows.Count;)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(1000, rows.Count - offset);
            await using var command = (OracleCommand)Command(database, connection, sql, transaction);
            if (!TryBindOracleArray(command, group, columns, rows, offset, count, metadata))
            {
                // Auto mappings can change CLR type between samples. A heterogeneous column is
                // deliberately sent one row at a time instead of coercing 64-bit values to double.
                count = 1;
                command.Parameters.Clear();
                command.ArrayBindCount = 0;
                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    var column = columns[columnIndex];
                    AddValueParameter(command, ExternalDatabaseProvider.Oracle, $"p0_{columnIndex}", rows[offset].Values[column],
                        pointRows ? PointFieldType(column) : group.Columns[columnIndex].FieldType,
                        metadata[pointRows ? PointColumn(ExternalDatabaseProvider.Oracle, column) : column].DataType);
                }
            }
            try { await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
            catch (OracleException exception) when (pointRows)
            {
                // Array binding may execute successful elements before reporting failures. The
                // enclosing transaction is committed only if every reported failure is a proven
                // replay. Any other error causes the entire batch (including earlier chunks) to roll back.
                var failures = exception.Errors.Cast<OracleError>().Select(error => (error.Number, error.ArrayBindIndex)).ToArray();
                var indices = ReplayIndices(failures, count);
                if (indices is null) throw;
                foreach (var index in indices)
                    if (!await EventExistsAsync(database, transaction, connection, Table(group), rows[offset + index].EventId, token).ConfigureAwait(false)) throw;
            }
            offset += count;
        }
    }

    internal static int[]? ReplayIndices(IReadOnlyList<(int Number, int ArrayBindIndex)> errors, int count)
    {
        if (errors.Count == 0 || errors.Any(error => error.Number != 1 || error.ArrayBindIndex < 0 || error.ArrayBindIndex >= count)) return null;
        return errors.Select(error => error.ArrayBindIndex).Distinct().ToArray();
    }

    internal static bool TryBindOracleArray(OracleCommand command, DatabaseWriteGroupConfiguration group,
        IReadOnlyList<string> columns, IReadOnlyList<DatabaseWriteRow> rows, int offset, int count,
        IReadOnlyDictionary<string, DatabaseColumnInfo> metadata)
    {
        var pointRows = group.Mode == DatabaseStorageMode.PointRows;
        command.BindByName = true;
        command.ArrayBindCount = count;
        using var scalar = new OracleCommand();
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var column = columns[columnIndex];
            var databaseColumn = pointRows ? PointColumn(ExternalDatabaseProvider.Oracle, column) : column;
            var bindings = new OracleParameter[count];
            for (var rowIndex = 0; rowIndex < count; rowIndex++)
                bindings[rowIndex] = (OracleParameter)AddValueParameter(scalar, ExternalDatabaseProvider.Oracle, "v" + rowIndex,
                    rows[offset + rowIndex].Values[column], pointRows ? PointFieldType(column) : group.Columns[columnIndex].FieldType,
                    metadata[databaseColumn].DataType);
            var nonNull = bindings.Where(binding => binding.Value is not DBNull).ToArray();
            var prototype = nonNull.FirstOrDefault() ?? bindings[0];
            var elementType = nonNull.FirstOrDefault()?.Value.GetType() ?? prototype.OracleDbType switch
            {
                OracleDbType.Int16 => typeof(short), OracleDbType.Int32 => typeof(int), OracleDbType.Int64 => typeof(long),
                OracleDbType.Decimal => typeof(decimal), OracleDbType.Double or OracleDbType.BinaryDouble => typeof(double),
                OracleDbType.Single or OracleDbType.BinaryFloat => typeof(float), OracleDbType.Boolean => typeof(bool),
                OracleDbType.Date or OracleDbType.TimeStamp => typeof(DateTime), OracleDbType.TimeStampTZ => typeof(OracleTimeStampTZ),
                _ => typeof(string)
            };
            var text = elementType == typeof(string);
            if (nonNull.Any(binding => binding.Value.GetType() != elementType)
                || !text && nonNull.Any(binding => binding.OracleDbType != prototype.OracleDbType)) return false;
            var oracleType = text && nonNull.Any(binding => binding.OracleDbType == OracleDbType.NClob) ? OracleDbType.NClob : prototype.OracleDbType;
            var values = Array.CreateInstance(elementType, count);
            var statuses = new OracleParameterStatus[count];
            var sizes = new int[count];
            for (var rowIndex = 0; rowIndex < count; rowIndex++)
            {
                var value = bindings[rowIndex].Value;
                if (value is DBNull) statuses[rowIndex] = OracleParameterStatus.NullInsert;
                else values.SetValue(value, rowIndex);
                sizes[rowIndex] = value switch { string valueText => Math.Max(1, valueText.Length), byte[] bytes => Math.Max(1, bytes.Length), _ => 1 };
            }
            var parameter = new OracleParameter($"p0_{columnIndex}", oracleType)
            { Value = values, ArrayBindStatus = statuses };
            if (text || elementType == typeof(byte[])) { parameter.ArrayBindSize = sizes; parameter.Size = sizes.Max(); }
            command.Parameters.Add(parameter);
            scalar.Parameters.Clear();
        }
        return true;
    }
}
