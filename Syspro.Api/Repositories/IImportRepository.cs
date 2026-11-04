using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

// Every operation that changes a claimed job takes its fencing token, and does nothing
// unless that token is still current. A worker whose job was reclaimed holds an old token,
// so it can no longer affect the job.
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

    // Must be the first statement of a batch's transaction. Returns false if this worker
    // no longer owns the job. Otherwise the job's row stays locked until the transaction
    // ends, so a reclaim either waits for this batch to commit, or has already changed the
    // token and this returns false before anything is written.
    Task<bool> RenewLeaseAsync(
        int id,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    // Advances the counters, and so the checkpoint, and resets the failure count. Returns
    // false, changing nothing, unless the job is Processing with this token and the expected
    // checkpoint. Completing the job here means it finishes in the same transaction as its
    // last batch.
    Task<bool> ApplyBatchProgressAsync(
        int id,
        long fencingToken,
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

    // Atomically claims the job that should run next, or returns null if there is none or
    // another worker holds a live lease. A job whose lease has expired comes first, then
    // the oldest pending job, and only one job can be Processing at a time, so jobs run in
    // order. The returned job carries its new fencing token and persisted checkpoint. It
    // is not tracked, because tracking is cleared between batches.
    Task<ImportLog?> ClaimNextJobAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    // Hands a job interrupted by shutdown back to Pending, without counting it as a failure.
    Task ReleaseJobAsync(int id, long fencingToken, CancellationToken cancellationToken);

    // Hands a job that hit a transient failure back to Pending, keeping its failure count
    // so that the retry limit applies.
    Task RequeueJobAsync(int id, long fencingToken, CancellationToken cancellationToken);

    // A Failed job is never retried, so its stored input is removed in the same transaction.
    Task MarkJobFailedAsync(
        int id,
        long fencingToken,
        string reason,
        CancellationToken cancellationToken);
}

public readonly record struct ImportBatchProgress(int Processed, int Created, int Updated, int Failed);
