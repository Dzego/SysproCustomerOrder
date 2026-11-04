using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.Models;
using Syspro.Api.Repositories;

namespace Syspro.Tests.TestSupport;

// Lets the import's rules run against EF Core InMemory, which has no transactions or bulk
// updates. It cannot roll back, so rollback is verified against SQL Server instead.
public sealed class InMemoryImportRepository(AppDbContext dbContext) : IImportRepository
{
    public int TransactionCount { get; private set; }

    public bool IsInTransaction { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        TransactionCount++;
        IsInTransaction = true;

        try
        {
            await work(cancellationToken);
        }
        finally
        {
            IsInTransaction = false;
            dbContext.ChangeTracker.Clear();
        }
    }

    public void AddErrors(IEnumerable<ImportError> errors)
    {
        dbContext.ImportErrors.AddRange(errors);
    }

    public Task<bool> RenewLeaseAsync(
        int id,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public async Task<bool> ApplyBatchProgressAsync(
        int id,
        long fencingToken,
        int expectedProcessedCount,
        ImportBatchProgress progress,
        bool completeJob,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.ImportLogs.SingleAsync(x => x.Id == id, cancellationToken);

        if (job.Status != ImportJobStatus.Processing || job.ProcessedCount != expectedProcessedCount)
        {
            return false;
        }

        job.ProcessedCount += progress.Processed;
        job.CreatedCount += progress.Created;
        job.UpdatedCount += progress.Updated;
        job.FailedCount += progress.Failed;
        job.ConsecutiveFailureCount = 0;

        if (completeJob)
        {
            job.Status = ImportJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
        }

        return true;
    }

    public void RemoveJobInput(int id)
    {
        dbContext.ImportJobInputs.Remove(new ImportJobInput { ImportLogId = id, Content = [] });
    }

    public Task AddLogAsync(ImportLog importLog, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ImportLog?> GetJobAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Stream?> OpenJobInputAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ImportLog?> ClaimNextJobAsync(string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ReleaseJobAsync(int id, long fencingToken, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RequeueJobAsync(int id, long fencingToken, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task MarkJobFailedAsync(int id, long fencingToken, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
}
