using Microsoft.EntityFrameworkCore;
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

    public Task MarkJobFailedAsync(int id, string reason, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        return _dbContext.ImportLogs
            .Where(job => job.Id == id && job.Status == ImportJobStatus.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ImportJobStatus.Failed)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.FailureReason, reason),
                cancellationToken);
    }

    public async Task<InterruptedJobRecovery> RecoverInterruptedJobsAsync(
        int maxAttempts,
        string exhaustedReason,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

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

        return new InterruptedJobRecovery(requeued, failed);
    }
}
