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

    public async Task<ImportLog?> ClaimNextPendingJobAsync(CancellationToken cancellationToken)
    {
        var candidateId = await _dbContext.ImportLogs
            .Where(job => job.Status == ImportJobStatus.Pending)
            .OrderBy(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .Select(job => (int?)job.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidateId is null)
        {
            return null;
        }

        // The status check makes this atomic: only one caller can update the row.
        var now = DateTime.UtcNow;

        var claimed = await _dbContext.ImportLogs
            .Where(job => job.Id == candidateId && job.Status == ImportJobStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ImportJobStatus.Processing)
                    .SetProperty(job => job.StartedAt, now)
                    .SetProperty(job => job.AttemptCount, job => job.AttemptCount + 1),
                cancellationToken);

        if (claimed == 0)
        {
            return null;
        }

        return await _dbContext.ImportLogs
            .SingleAsync(job => job.Id == candidateId, cancellationToken);
    }

    public Task ReleaseJobAsync(int id, CancellationToken cancellationToken)
    {
        return _dbContext.ImportLogs
            .Where(job => job.Id == id && job.Status == ImportJobStatus.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ImportJobStatus.Pending)
                    .SetProperty(job => job.StartedAt, (DateTime?)null)
                    .SetProperty(job => job.AttemptCount, job => job.AttemptCount - 1),
                cancellationToken);
    }

    public async Task MarkJobFailedAsync(int id, string reason, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        await _dbContext.ImportLogs
            .Where(job => job.Id == id && job.Status == ImportJobStatus.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ImportJobStatus.Failed)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.FailureReason, reason),
                cancellationToken);

        await DeleteFinishedJobInputsAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<InterruptedJobRecovery> RecoverInterruptedJobsAsync(
        int maxAttempts,
        string exhaustedReason,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Runs first so that exhausted jobs are not requeued by the next statement.
        var failed = await _dbContext.ImportLogs
            .Where(job => job.Status == ImportJobStatus.Processing && job.AttemptCount >= maxAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ImportJobStatus.Failed)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.FailureReason, exhaustedReason),
                cancellationToken);

        var requeued = await _dbContext.ImportLogs
            .Where(job => job.Status == ImportJobStatus.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ImportJobStatus.Pending)
                    .SetProperty(job => job.StartedAt, (DateTime?)null),
                cancellationToken);

        await DeleteFinishedJobInputsAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new InterruptedJobRecovery(requeued, failed);
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
