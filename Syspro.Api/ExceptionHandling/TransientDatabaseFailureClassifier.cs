using Microsoft.Data.SqlClient;

namespace Syspro.Api.ExceptionHandling;

// Decides whether trying again could succeed. A wrong "transient" costs a limited number
// of retries, while a wrong "not transient" fails the job, so this errs towards retrying.
public static class TransientDatabaseFailureClassifier
{
    // Errors of this severity end the connection, which includes a lost server.
    private const byte FatalErrorClass = 20;

    private static readonly HashSet<int> TransientErrorNumbers =
    [
        -2,     // Command timeout
        20,     // The instance does not support encryption or is unavailable
        64,     // The specified network name is no longer available
        233,    // No process is on the other end of the pipe
        1205,   // Chosen as a deadlock victim
        1222,   // Lock request timeout
        10053,  // Connection aborted
        10054,  // Connection reset
        10060,  // Connection timed out
        40197,  // Azure SQL: error processing the request
        40501,  // Azure SQL: service is busy
        40613,  // Azure SQL: database is not currently available
        49918,  // Azure SQL: not enough resources
        49919,  // Azure SQL: too many operations in progress
        49920   // Azure SQL: too many operations in progress
    ];

    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException && IsTransient(sqlException))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransient(SqlException exception)
    {
        return exception.Class >= FatalErrorClass ||
               exception.Errors.Cast<SqlError>().Any(error => TransientErrorNumbers.Contains(error.Number));
    }
}
