using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.ExceptionHandling;
using Syspro.Api.Models;
using Syspro.Api.Repositories;

namespace Syspro.Api.Services;

// Failure reasons are returned to clients, so they are fixed strings chosen here, plus
// line numbers, and never exception messages. The exception itself goes only to the logs.
public sealed class CustomerImportJobRunner : ICustomerImportJobRunner
{
    private const string InputMissingReason =
        "The import job has no stored input, so it could not run. Submit a new import.";

    private const string DatabaseFailureReason =
        "The import could not be saved to the database.";

    private const string UnexpectedFailureReason =
        "The import failed unexpectedly.";

    private const string RetriesExhaustedReason =
        "The import kept failing on a temporary database problem and will not be retried.";

    private const string AttemptsExhaustedReason =
        "The import was interrupted too many times and will not be retried.";

    // For diagnosis only. Ownership is enforced by the fencing token, not by this name.
    private static readonly string WorkerName = CreateWorkerName();

    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    private readonly IImportRepository _importRepository;
    private readonly ICustomerImportService _importService;
    private readonly CustomerImportWorkerOptions _options;
    private readonly TimeSpan _leaseDuration;
    private readonly ILogger<CustomerImportJobRunner> _logger;

    public CustomerImportJobRunner(
        IImportRepository importRepository,
        ICustomerImportService importService,
        IOptions<CustomerImportWorkerOptions> options,
        IOptions<CustomerImportOptions> importOptions,
        ILogger<CustomerImportJobRunner> logger)
    {
        _importRepository = importRepository;
        _importService = importService;
        _options = options.Value;
        _leaseDuration = importOptions.Value.LeaseDuration;
        _logger = logger;
    }

    public async Task<bool> RunNextJobAsync(CancellationToken stoppingToken)
    {
        var job = await _importRepository.ClaimNextJobAsync(WorkerName, _leaseDuration, stoppingToken);

        if (job is null)
        {
            return false;
        }

        var jobId = job.Id;
        var fencingToken = job.FencingToken;

        _logger.LogInformation(
            "Started import job {JobId}, attempt {AttemptCount}, token {FencingToken}, after {CommittedLines} committed lines",
            jobId,
            job.AttemptCount,
            fencingToken,
            job.ProcessedCount);

        // The count includes this claim, so this job has already had MaxAttempts executions
        // with no progress. It stops here a job that crashes the process from doing so forever.
        if (job.ConsecutiveFailureCount > _options.MaxAttempts)
        {
            _logger.LogError(
                "Import job {JobId} was interrupted {ConsecutiveFailureCount} times with no progress and was marked as failed",
                jobId,
                job.ConsecutiveFailureCount - 1);

            await _importRepository.MarkJobFailedAsync(
                jobId,
                fencingToken,
                AttemptsExhaustedReason + DescribeProgress(job.ProcessedCount),
                stoppingToken);

            return true;
        }

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

                await _importRepository.MarkJobFailedAsync(
                    jobId,
                    fencingToken,
                    InputMissingReason + DescribeProgress(job.ProcessedCount),
                    stoppingToken);

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
            await HandleShutdownAsync(jobId, fencingToken, exception);
        }
        catch (ImportJobStateChangedException exception)
        {
            _logger.LogWarning(exception, "Import job {JobId} was reclaimed by another worker, so this one stopped", jobId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Import job {JobId} failed", jobId);

            await HandleFailureAsync(jobId, fencingToken, exception, stoppingToken);
        }

        return true;
    }

    // Reads the job back rather than trusting what this execution last saw. A lost
    // connection can hide a COMMIT that succeeded, so the database decides what happened.
    private async Task HandleFailureAsync(
        int jobId,
        long fencingToken,
        Exception exception,
        CancellationToken stoppingToken)
    {
        var job = await _importRepository.GetJobAsync(jobId, stoppingToken);

        if (job is null || job.Status != ImportJobStatus.Processing)
        {
            _logger.LogInformation(
                "Import job {JobId} is already {Status}, so its last commit succeeded despite the error",
                jobId,
                job?.Status);

            return;
        }

        if (job.FencingToken != fencingToken)
        {
            _logger.LogWarning("Import job {JobId} has been reclaimed by another worker, so this one will not change it", jobId);

            return;
        }

        var isTransient = TransientDatabaseFailureClassifier.IsTransient(exception);

        if (isTransient && job.ConsecutiveFailureCount < _options.MaxAttempts)
        {
            _logger.LogWarning(
                "Import job {JobId} will resume after {CommittedLines} committed lines. Failures in a row: {ConsecutiveFailureCount}",
                jobId,
                job.ProcessedCount,
                job.ConsecutiveFailureCount);

            await _importRepository.RequeueJobAsync(jobId, fencingToken, stoppingToken);

            return;
        }

        var reason = isTransient ? RetriesExhaustedReason : GetFailureReason(exception);

        await _importRepository.MarkJobFailedAsync(
            jobId,
            fencingToken,
            reason + DescribeProgress(job.ProcessedCount),
            stoppingToken);
    }

    private static string CreateWorkerName()
    {
        // The unique part comes first, so that a long machine name cannot truncate it.
        var name = $"{Guid.NewGuid():N}:{Environment.ProcessId}:{Environment.MachineName}";

        return name[..Math.Min(name.Length, ImportLog.LeaseOwnerMaxLength)];
    }

    private static string DescribeProgress(int committedLines)
    {
        return committedLines == 0
            ? " No customers were changed."
            : $" Lines 1 to {committedLines} were committed and remain applied. " +
              $"Processing stopped at line {committedLines + 1}.";
    }

    // Requeues whatever the exception was: the batch in progress was rolled back, and if
    // this is a genuine fault the next run will report it as a failure. Failing to requeue
    // is also safe, because the job stays Processing until its lease expires.
    private async Task HandleShutdownAsync(int jobId, long fencingToken, Exception exception)
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

            await _importRepository.ReleaseJobAsync(jobId, fencingToken, timeout.Token);
        }
        catch (Exception releaseException)
        {
            _logger.LogWarning(
                releaseException,
                "Could not requeue import job {JobId} during shutdown. Another worker can take it when its lease expires",
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
