using System.Net.Sockets;
using Npgsql;

namespace Fynydd.Umbraco.Search.Postgres.VectorStores;

/// <summary>Identifies temporary database outages, without hiding SQL or configuration errors.</summary>
internal static class PostgresTransientFailure
{
    public static bool IsTransient(Exception exception)
    {
        if (exception is PostgresException postgresException)
            return postgresException.IsTransient;

        if (exception is NpgsqlException { IsTransient: true } or OperationCanceledException or TimeoutException or SocketException or HttpRequestException)
            return true;

        return exception.InnerException is not null && IsTransient(exception.InnerException);
    }
}
