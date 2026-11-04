using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.ExceptionHandling;
using Syspro.Api.Repositories;

namespace Syspro.Api.Services;

// Failure reasons are returned to clients, so they are fixed strings chosen here
// and never exception messages. The exception itself goes only to the logs.
public sealed class CustomerImportJobRunner : ICustomerImportJobRunner
{
    private const string InputMissingReason =
        "The import job has no stored input, so it could not run. No customers were changed. Submit a new import.";

    private const string DatabaseFailureReason =
        "The import could not be saved to the database. No customers were changed.";

    private const string UnexpectedFailureReason =
        "The import failed unexpectedly. No customers were changed.";

    private const string AttemptsExhaustedReason =
        "The import was interrupted too many times and will not be retried. No customers were changed.";

    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    private readonly IImportRepository _importRepository;
    private readonly ICustomerImportService _importService;
    private readonly CustomerImportWorkerOptions _options;
    private readonly ILogger<CustomerImportJobRunner> _logger;

    public CustomerImportJobRunner(
        IImportRepository importRepository,
        ICustomerImportService importService,
        IOptions<CustomerImportWorkerOptions> options,
        ILogger<CustomerImportJobRunner> logger)
    {
        _importRepository = importRepository;
        _importService = importService;
        _options = options.Value;
        _logger = logger;
    }

    // A Processing job that nothing is running has not changed any customers,
    // because Completed commits in the same transaction as the customers. Running
    // it again is therefore safe. The attempt limit stops a job that crashes the
    // process from doing so forever.
    public async Task RecoverInterruptedJobsAsync(CancellationToken stoppingToken)
    {
        var recovery = await _importRepository.RecoverInterruptedJobsAsync(
            _options.MaxAttempts,
            AttemptsExhaustedReason,
            stoppingToken);

        if (recovery.Requeued > 0 || recovery.Failed > 0)
        {
            _logger.LogWarning(
                "Recovered interrupted import jobs: {RequeuedCount} requeued, {FailedCount} failed after {MaxAttempts} attempts",
                recovery.Requeued,
                recovery.Failed,
                _options.MaxAttempts);
        }
    }

    public async Task<bool> RunNextJobAsync(CancellationToken stoppingToken)
    {
        var job = await _importRepository.ClaimNextPendingJobAsync(stoppingToken);

        if (job is null)
        {
            return false;
        }

        var jobId = job.Id;

        _logger.LogInformation(
            "Started import job {JobId}, attempt {AttemptCount}",
            jobId,
            job.AttemptCount);

        try
        {
            // Deliberately has no access to the file. A job without its stored input
            // must fail, because the file may no longer be what was accepted.
            await using var input = await _importRepository.OpenJobInputAsync(jobId, stoppingToken);

            if (input is null)
            {
                _logger.LogError(
                    "Import job {JobId} has no stored input and was marked as failed. It was probably accepted before input snapshots were introduced",
                    jobId);

                await _importRepository.MarkJobFailedAsync(jobId, InputMissingReason, stoppingToken);

                return true;
            }

            var result = await _importService.ImportAsync(job, input, stoppingToken);

            _logger.LogInformation(
                "Completed import job {JobId}: {ProcessedCount} processed, {CreatedCount} created, {UpdatedCount} updated, {FailedCount} failed",
                jobId,
                result.Processed,
                result.Created,
                result.Updated,
                result.Failed);
        }
        catch (Exception exception) when (stoppingToken.IsCancellationRequested)
        {
            await HandleShutdownAsync(jobId, exception);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Import job {JobId} failed", jobId);

            await _importRepository.MarkJobFailedAsync(jobId, GetFailureReason(exception), stoppingToken);
        }

        return true;
    }

    // Requeues whatever the exception was: nothing was committed, and if this is a
    // genuine fault the next run will report it as a failure. Failing to requeue is
    // also safe, because the job stays Processing and is recovered at next startup.
    private async Task HandleShutdownAsync(int jobId, Exception exception)
    {
        if (CancellationExceptionClassifier.IsCancellation(exception))
        {
            _logger.LogInformation("Import job {JobId} was interrupted by shutdown", jobId);
        }
        else
        {
            _logger.LogError(exception, "Import job {JobId} failed during shutdown", jobId);
        }

        try
        {
            using var timeout = new CancellationTokenSource(ReleaseTimeout);

            await _importRepository.ReleaseJobAsync(jobId, timeout.Token);
        }
        catch (Exception releaseException)
        {
            _logger.LogWarning(
                releaseException,
                "Could not requeue import job {JobId} during shutdown. It will be recovered at next startup",
                jobId);
        }
    }

    private static string GetFailureReason(Exception exception)
    {
        return exception switch
        {
            DbUpdateException => DatabaseFailureReason,
            _ => UnexpectedFailureReason
        };
    }
}
