using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Syspro.Api.Data;
using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

public class ImportRepository : IImportRepository
{
    private readonly AppDbContext _dbContext;

    public ImportRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddLogAsync(ImportLog importLog, CancellationToken cancellationToken)
    {
        await _dbContext.ImportLogs.AddAsync(importLog, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            await work(cancellationToken);

            // All the work is done, so shutdown should not abandon the commit. Cancelling
            // here would only make its outcome uncertain.
            await transaction.CommitAsync(CancellationToken.None);
        }
        finally
        {
            _dbContext.ChangeTracker.Clear();
        }
    }

    public void AddErrors(IEnumerable<ImportError> errors)
    {
        _dbContext.ImportErrors.AddRange(errors);
    }

    public async Task<bool> RenewLeaseAsync(
        int id,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var leaseMilliseconds = (int)leaseDuration.TotalMilliseconds;

        // Uses the database clock, so that clock differences between servers do not matter.
        var updated = await _dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE [ImportLogs]
             SET [LeaseExpiresAt] = DATEADD(millisecond, {leaseMilliseconds}, SYSUTCDATETIME())
             WHERE [Id] = {id} AND [FencingToken] = {fencingToken} AND [Status] = 'Processing'
             """,
            cancellationToken);

        return updated == 1;
    }

    public async Task<bool> ApplyBatchProgressAsync(
        int id,
        long fencingToken,
        int expectedProcessedCount,
        ImportBatchProgress progress,
        bool completeJob,
        CancellationToken cancellationToken)
    {
        var status = completeJob ? ImportJobStatus.Completed : ImportJobStatus.Processing;
        DateTime? completedAt = completeJob ? DateTime.UtcNow : null;

        var updated = await OwnedJob(id, fencingToken)
            .Where(job => job.ProcessedCount == expectedProcessedCount)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.ProcessedCount, job => job.ProcessedCount + progress.Processed)
                    .SetProperty(job => job.CreatedCount, job => job.CreatedCount + progress.Created)
                    .SetProperty(job => job.UpdatedCount, job => job.UpdatedCount + progress.Updated)
                    .SetProperty(job => job.FailedCount, job => job.FailedCount + progress.Failed)
                    .SetProperty(job => job.ConsecutiveFailureCount, 0)
                    .SetProperty(job => job.Status, status)
                    .SetProperty(job => job.CompletedAt, completedAt)
                    .SetProperty(job => job.LeaseOwner, job => completeJob ? null : job.LeaseOwner)
                    .SetProperty(job => job.LeaseExpiresAt, job => completeJob ? null : job.LeaseExpiresAt),
                cancellationToken);

        return updated == 1;
    }

    private IQueryable<ImportLog> OwnedJob(int id, long fencingToken)
    {
        return _dbContext.ImportLogs.Where(job =>
            job.Id == id &&
            job.FencingToken == fencingToken &&
            job.Status == ImportJobStatus.Processing);
    }

    public Task<ImportLog?> GetJobAsync(int id, CancellationToken cancellationToken)
    {
        return _dbContext.ImportLogs
            .AsNoTracking()
            .FirstOrDefaultAsync(job => job.Id == id, cancellationToken);
    }

    public async Task<Stream?> OpenJobInputAsync(int id, CancellationToken cancellationToken)
    {
        var length = await _dbContext.ImportJobInputs
            .Where(input => input.ImportLogId == id)
            .Select(input => EF.Functions.DataLength(input.Content))
            .SingleOrDefaultAsync(cancellationToken);

        return length is null
            ? null
            : new ImportJobInputStream(
                length.Value,
                (offset, destination, token) => ReadJobInputChunkAsync(id, offset, destination, token));
    }

    // Uses SqlClient directly, because reading a large value through EF Core is far
    // slower: its read time grows much faster than the size of the value. The reader
    // is closed before this returns, so the context's connection is free for its next
    // query. MARS is not enabled, so an open reader would make that query fail.
    private async Task<int> ReadJobInputChunkAsync(
        int id,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = _dbContext.Database.GetDbConnection().CreateCommand();

            command.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandTimeout = _dbContext.Database.GetCommandTimeout() ?? command.CommandTimeout;
            command.CommandText =
                "SELECT SUBSTRING([Content], @start, @length) FROM [ImportJobInputs] WHERE [ImportLogId] = @id";

            AddParameter(command, "@start", DbType.Int64, offset + 1);
            AddParameter(command, "@length", DbType.Int32, destination.Length);
            AddParameter(command, "@id", DbType.Int32, id);

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess,
                cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return 0;
            }

            await using var content = reader.GetStream(0);

            var total = 0;
            int read;

            while (total < destination.Length &&
                   (read = await content.ReadAsync(destination[total..], cancellationToken)) > 0)
            {
                total += read;
            }

            return total;
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;

        command.Parameters.Add(parameter);
    }

    public void RemoveJobInput(int id)
    {
        _dbContext.ImportJobInputs.Remove(new ImportJobInput { ImportLogId = id, Content = [] });
    }

    public async Task<ImportLog?> ClaimNextJobAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var leaseMilliseconds = (int)leaseDuration.TotalMilliseconds;

        // One transaction, so it is atomic. The locks serialize concurrent claims, and they
        // also make a reclaim wait for a batch that holds the job's row. The candidate is
        // always the Processing job if there is one, so a live lease stops anything else
        // from starting, which keeps jobs in order.
        //
        // SYSUTCDATETIME() is fixed when a statement starts, so the first statement decides
        // using the time before any wait for the lock. The second one starts the lease once
        // the row is held, otherwise a long wait would use it up before the worker began.
        // XACT_ABORT rolls back on any error, including a command timeout, and the row is
        // only returned after the commit, so a caller never sees a claim that did not happen.
        var claimed = await _dbContext.ImportLogs
            .FromSql(
                $"""
                 SET NOCOUNT ON;
                 SET XACT_ABORT ON;

                 DECLARE @Claimed TABLE ([Id] int NOT NULL);

                 BEGIN TRANSACTION;

                 WITH [Candidate] AS (
                     SELECT TOP (1) *
                     FROM [ImportLogs] WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
                     WHERE [Status] IN ('Processing', 'Pending')
                     ORDER BY CASE WHEN [Status] = 'Processing' THEN 0 ELSE 1 END, [CreatedAt], [Id]
                 )
                 UPDATE [Candidate]
                 SET [Status] = 'Processing',
                     [LeaseOwner] = {leaseOwner},
                     [LeaseExpiresAt] = DATEADD(millisecond, {leaseMilliseconds}, SYSUTCDATETIME()),
                     [FencingToken] = [FencingToken] + 1,
                     [AttemptCount] = [AttemptCount] + 1,
                     [ConsecutiveFailureCount] = [ConsecutiveFailureCount] + 1,
                     [StartedAt] = SYSUTCDATETIME()
                 OUTPUT inserted.[Id] INTO @Claimed
                 WHERE [Status] = 'Pending' OR [LeaseExpiresAt] < SYSUTCDATETIME();

                 UPDATE [ImportLogs]
                 SET [LeaseExpiresAt] = DATEADD(millisecond, {leaseMilliseconds}, SYSUTCDATETIME())
                 WHERE [Id] IN (SELECT [Id] FROM @Claimed);

                 COMMIT TRANSACTION;

                 SELECT * FROM [ImportLogs] WHERE [Id] IN (SELECT [Id] FROM @Claimed);
                 """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    public Task ReleaseJobAsync(int id, long fencingToken, CancellationToken cancellationToken)
    {
        return OwnedJob(id, fencingToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ImportJobStatus.Pending)
                    .SetProperty(job => job.StartedAt, (DateTime?)null)
                    .SetProperty(job => job.LeaseOwner, (string?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(
                        job => job.ConsecutiveFailureCount,
                        job => job.ConsecutiveFailureCount > 0 ? job.ConsecutiveFailureCount - 1 : 0),
                cancellationToken);
    }

    public Task RequeueJobAsync(int id, long fencingToken, CancellationToken cancellationToken)
    {
        return OwnedJob(id, fencingToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ImportJobStatus.Pending)
                    .SetProperty(job => job.StartedAt, (DateTime?)null)
                    .SetProperty(job => job.LeaseOwner, (string?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null),
                cancellationToken);
    }

    public async Task MarkJobFailedAsync(
        int id,
        long fencingToken,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        await OwnedJob(id, fencingToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ImportJobStatus.Failed)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.FailureReason, reason)
                    .SetProperty(job => job.LeaseOwner, (string?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null),
                cancellationToken);

        await DeleteFinishedJobInputsAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    // Selects by status, so this can never remove the input of a job that may still run.
    private Task DeleteFinishedJobInputsAsync(CancellationToken cancellationToken)
    {
        return _dbContext.ImportJobInputs
            .Where(input =>
                input.ImportLog.Status == ImportJobStatus.Completed ||
                input.ImportLog.Status == ImportJobStatus.Failed)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
