using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Syspro.IntegrationTests.Infrastructure;

// Cancels at the moment SaveChanges starts, after any earlier queries succeeded.
public sealed class CancelOnSaveInterceptor(CancellationTokenSource source) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        source.Cancel();

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

// Holds every query open on the server, so that a test can cancel a command that
// SQL Server is really executing rather than one that never started.
public sealed class SlowQueryInterceptor(string? onlyCommandsContaining = null) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (onlyCommandsContaining is null ||
            command.CommandText.Contains(onlyCommandsContaining, StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = "WAITFOR DELAY '00:00:30';" + command.CommandText;
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

// Tells a test that the API has started running a query.
public sealed class SignalOnQueryInterceptor(TaskCompletionSource signal) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        signal.TrySetResult();

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
