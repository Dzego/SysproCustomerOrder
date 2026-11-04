using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

public interface IImportRepository
{
    Task AddLogAsync(ImportLog importLog, CancellationToken cancellationToken);

    // Also saves customer changes made through ICustomerRepository, which shares this
    // repository's scoped context.
    Task SaveChangesAsync(CancellationToken cancellationToken);

    // Runs the work in one explicit transaction and commits it. Everything is rolled
    // back if the work fails, and change tracking is cleared either way, so tracking
    // never holds more than one batch.
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken);

    void AddErrors(IEnumerable<ImportError> errors);

    // Advances the counters, and so the checkpoint, and resets the failure count. Returns
    // false, changing nothing, unless the job is Processing with the expected checkpoint.
    // Completing the job here means it finishes in the same transaction as its last batch.
    Task<bool> ApplyBatchProgressAsync(
        int id,
        int expectedProcessedCount,
        ImportBatchProgress progress,
        bool completeJob,
        CancellationToken cancellationToken);

    Task<ImportLog?> GetJobAsync(int id, CancellationToken cancellationToken);

    // Returns a stream that reads the stored input in chunks, or null when the job
    // has none. The stream queries through this repository's context.
    Task<Stream?> OpenJobInputAsync(int id, CancellationToken cancellationToken);

    // Takes effect at the next SaveChangesAsync, so that the input is removed in the
    // same transaction as whatever finishes the job.
    void RemoveJobInput(int id);

    // Returns the oldest pending job after atomically moving it to Processing, or null
    // when there is none. The returned job carries the persisted checkpoint. It is not
    // tracked, because tracking is cleared between batches.
    Task<ImportLog?> ClaimNextPendingJobAsync(CancellationToken cancellationToken);

    // Moves a job that was interrupted by shutdown back to Pending, without counting it
    // as a failure. Does nothing if the job is no longer Processing.
    Task ReleaseJobAsync(int id, CancellationToken cancellationToken);

    // Moves a job that hit a transient failure back to Pending, keeping its failure count
    // so that the retry limit applies. Does nothing if the job is no longer Processing.
    Task RequeueJobAsync(int id, CancellationToken cancellationToken);

    // Does nothing if the job is no longer Processing. A Failed job is never retried,
    // so its stored input is removed in the same transaction.
    Task MarkJobFailedAsync(int id, string reason, CancellationToken cancellationToken);

    // Treats every Processing job as abandoned, so it must only run while this
    // worker is idle and no other worker is running. Requeued jobs keep their checkpoint
    // and stored input. Jobs at the failure limit become Failed and lose their input.
    Task<InterruptedJobRecovery> RecoverInterruptedJobsAsync(
        int maxAttempts,
        string exhaustedReason,
        CancellationToken cancellationToken);
}

public readonly record struct InterruptedJobRecovery(int Requeued, int Failed);

public readonly record struct ImportBatchProgress(int Processed, int Created, int Updated, int Failed);
