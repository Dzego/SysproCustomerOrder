using System.Collections.Concurrent;
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
// worker immediately ahead of another worker's update.
public sealed class BeforeFirstUpdateInterceptor(Func<Task> action) : DbCommandInterceptor
{
    private bool _hasRun;

    // A claim is one statement that returns the claimed row, so it runs as a reader.
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (!_hasRun && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            _hasRun = true;

            await action();
        }

        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

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

// Fails a real statement partway through a save, after earlier statements have run.
public sealed class FailCommandInterceptor(string commandContaining, int occurrence = 1) : DbCommandInterceptor
{
    public sealed class CommandFailedException : Exception;

    private int _matches;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Fail(command);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Fail(command);

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Fail(DbCommand command)
    {
        if (command.CommandText.Contains(commandContaining, StringComparison.OrdinalIgnoreCase) &&
            ++_matches == occurrence)
        {
            throw new CommandFailedException();
        }
    }
}

// Records the SQL that reads and saves run, to prove what they do not touch.
public sealed class CommandTextRecorder : DbCommandInterceptor
{
    public ConcurrentQueue<string> Commands { get; } = new();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Enqueue(command.CommandText);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

// Throws at the start of a chosen save, as a non-transient failure in that batch.
public sealed class ThrowOnSaveInterceptor(int saveNumber) : SaveChangesInterceptor
{
    public sealed class InjectedFailureException : Exception
    {
        public InjectedFailureException()
            : base("Server=prod-sql;Password=hunter2")
        {
        }
    }

    private int _saves;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (++_saves == saveNumber)
        {
            throw new InjectedFailureException();
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

// Cancels at the start of a chosen save, as shutdown would between two statements.
public sealed class CancelOnSaveNumberInterceptor(CancellationTokenSource source, int saveNumber) : SaveChangesInterceptor
{
    private int _saves;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (++_saves == saveNumber)
        {
            source.Cancel();
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

// Ends the real connection with a fatal SQL Server error, either just before a chosen
// commit, so that SQL Server rolls it back, or just after, so that it has succeeded but
// the caller sees an error. The second case is what an ambiguous commit looks like.
public sealed class FatalErrorAroundCommitInterceptor(int commitNumber, bool afterCommit) : DbTransactionInterceptor
{
    private int _commits;
    private DbConnection? _connection;

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        // A committed transaction no longer knows its connection.
        _connection = transaction.Connection;

        if (!afterCommit && ++_commits == commitNumber)
        {
            await RaiseFatalErrorAsync(_connection!, transaction);
        }

        return await base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }

    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (afterCommit && ++_commits == commitNumber)
        {
            await RaiseFatalErrorAsync(_connection!, null);
        }

        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    private static async Task RaiseFatalErrorAsync(DbConnection connection, DbTransaction? transaction)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = "RAISERROR('Simulated loss of connection', 20, 1) WITH LOG";

        await command.ExecuteNonQueryAsync();
    }
}

// Records the most entities the change tracker held at the end of any save.
public sealed class TrackedEntityRecorder : SaveChangesInterceptor
{
    public int MaxTracked { get; private set; }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        MaxTracked = Math.Max(MaxTracked, eventData.Context!.ChangeTracker.Entries().Count());

        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }
}

// Runs an action straight after a chosen commit, when the batch is durable and its row lock
// has been released. It places another worker between two batches of this one.
public sealed class AfterCommitInterceptor(int commitNumber, Func<Task> action) : DbTransactionInterceptor
{
    private int _commits;

    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (++_commits == commitNumber)
        {
            await action();
        }

        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }
}

// Holds a chosen save open until a test releases it. By then the batch's fence has run, so
// its transaction holds the job's row, which lets a test place another worker behind it.
public sealed class PauseOnSaveInterceptor(int saveNumber) : SaveChangesInterceptor
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _saves;

    public Task Reached => _reached.Task;

    public void Release()
    {
        _release.TrySetResult();
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (++_saves == saveNumber)
        {
            _reached.TrySetResult();

            await _release.Task;
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
