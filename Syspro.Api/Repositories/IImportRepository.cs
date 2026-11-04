using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

public interface IImportRepository
{
    Task AddLogAsync(ImportLog importLog, CancellationToken cancellationToken);

    // Also saves customer changes made through ICustomerRepository, which shares this
    // repository's scoped context. This is what makes an import a single transaction.
    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<ImportLog?> GetJobAsync(int id, CancellationToken cancellationToken);

    // Returns null when the job has no stored input.
    Task<byte[]?> GetJobInputAsync(int id, CancellationToken cancellationToken);

    // Takes effect at the next SaveChangesAsync, so that the input is removed in the
    // same transaction as whatever finishes the job.
    void RemoveJobInput(int id);

    // Returns the oldest pending job after atomically moving it to Processing, or
    // null when there is none. The returned job is tracked by the current context.
    Task<ImportLog?> ClaimNextPendingJobAsync(CancellationToken cancellationToken);

    // Moves a job that was interrupted by shutdown back to Pending, without
    // counting the attempt. Does nothing if the job is no longer Processing.
    Task ReleaseJobAsync(int id, CancellationToken cancellationToken);

    // Does nothing if the job is no longer Processing. A Failed job is never retried,
    // so its stored input is removed in the same transaction.
    Task MarkJobFailedAsync(int id, string reason, CancellationToken cancellationToken);

    // Treats every Processing job as abandoned, so it must only run while this
    // worker is idle and no other worker is running. Requeued jobs keep their stored
    // input, and jobs that have exhausted their attempts lose it.
    Task<InterruptedJobRecovery> RecoverInterruptedJobsAsync(
        int maxAttempts,
        string exhaustedReason,
        CancellationToken cancellationToken);
}

public readonly record struct InterruptedJobRecovery(int Requeued, int Failed);
