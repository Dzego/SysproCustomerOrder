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
public sealed class SignalOnQueryInterceptor(
    TaskCompletionSource signal,
    string? onlyCommandsContaining = null) : DbCommandInterceptor
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
            signal.TrySetResult();
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

// Runs an action just before the first UPDATE, which lets a test place a competing
// worker in the gap between another worker's read and its update.
public sealed class BeforeFirstUpdateInterceptor(Func<Task> action) : DbCommandInterceptor
{
    private bool _hasRun;

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (!_hasRun && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            _hasRun = true;

            await action();
        }

        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class SaveAndCommitCounter : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    public int SaveCount { get; private set; }

    public int CommitCount { get; private set; }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        SaveCount++;

        return ValueTask.FromResult(result);
    }

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        CommitCount++;

        return Task.CompletedTask;
    }
}

public sealed class FailOnCommitInterceptor : DbTransactionInterceptor
{
    public sealed class CommitFailedException : Exception;

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        throw new CommitFailedException();
    }
}
