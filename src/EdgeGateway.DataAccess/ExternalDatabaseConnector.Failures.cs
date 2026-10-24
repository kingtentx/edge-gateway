using System.Data.Common;
using System.Net.Sockets;
using Oracle.ManagedDataAccess.Client;

namespace EdgeGateway.DataAccess;

public sealed partial class ExternalDatabaseConnector
{
    // Provider exception messages/inner exceptions never leave the connector.
    internal static bool IsTransientFailure(Exception exception) => exception switch
    {
        OracleException oracle => IsTransientOracleCode(oracle.Number),
        DbException database => database.IsTransient,
        IOException or SocketException or TimeoutException => true,
        _ => false
    };

    // Conservative allow-list: credentials, privileges, syntax, missing schema,
    // datatype and constraint errors are deliberately not retried.
    internal static bool IsTransientOracleCode(int code) => Math.Abs((long)code) is
        54 or 60 or 1013 or 3113 or 3114 or 3135 or 12170 or 12514 or 12516 or 12518
        or 12520 or 12528 or 12537 or 12541 or 12543 or 12545 or 12547 or 12560 or 12571 or 25408;
}
