using Microsoft.Data.SqlClient;

namespace Syspro.Api.ExceptionHandling;

public static class CancellationExceptionClassifier
{
    // SqlClient raises these values itself when a running command is cancelled.
    // Errors from the server have a non-zero number, and a timeout uses -2.
    private const int ClientGeneratedErrorNumber = 0;
    private const byte OperationCancelledErrorClass = 11;
    private const string OperationCancelledMessage = "Operation cancelled by user";

    // Decides only whether the exception is a cancellation, not whose it was. The
    // inner exceptions are checked because EF Core wraps failures during a save
    // in DbUpdateException.
    public static bool IsCancellation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException ||
                current is SqlException sqlException && IsCancelledCommand(sqlException))
            {
                return true;
            }
        }

        return false;
    }

    // SqlClient has no dedicated error number for this, so the message is checked
    // as well. If a localized message fails to match, the exception is treated as
    // a genuine failure and logged as an error, which is the safe direction.
    private static bool IsCancelledCommand(SqlException exception)
    {
        return exception.Errors.Cast<SqlError>().Any(error =>
            error.Number == ClientGeneratedErrorNumber &&
            error.Class == OperationCancelledErrorClass &&
            error.Message.Contains(OperationCancelledMessage, StringComparison.OrdinalIgnoreCase));
    }
}
