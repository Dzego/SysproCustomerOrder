using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.Tests.TestSupport;
using Xunit;

namespace Syspro.Tests.Services;

public class CustomerImportJobRunnerTests
{
    private const string SecretText = "Server=prod-sql;Password=hunter2";

    private readonly FakeRepository _repository = new();
    private readonly CapturingLogger<CustomerImportJobRunner> _logger = new();

    private sealed class FakeRepository : IImportRepository
    {
        public ImportLog? NextJob { get; set; }

        public Stream? StoredInput { get; set; } = new MemoryStream([1, 2, 3]);

        public int? ReleasedJobId { get; private set; }

        public CancellationToken ReleaseToken { get; private set; }

        public (int Id, string Reason)? FailedJob { get; private set; }

        public Exception? ReleaseFailure { get; set; }

        public int? RecoveryMaxAttempts { get; private set; }

        public string? RecoveryReason { get; private set; }

        public InterruptedJobRecovery RecoveryResult { get; set; }

        public Task AddLogAsync(ImportLog importLog, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> work,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void AddErrors(IEnumerable<ImportError> errors)
        {
            throw new NotSupportedException();
        }

        public Task<bool> ApplyBatchProgressAsync(
            int id,
            int expectedProcessedCount,
            ImportBatchProgress progress,
            bool completeJob,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ImportLog? PersistedJob { get; set; }

        public int? RequeuedJobId { get; private set; }

        public Task RequeueJobAsync(int id, CancellationToken cancellationToken)
        {
            RequeuedJobId = id;

            return Task.CompletedTask;
        }

        // What is stored can differ from what this execution last saw.
        public Task<ImportLog?> GetJobAsync(int id, CancellationToken cancellationToken)
        {
            return Task.FromResult(PersistedJob ?? NextJob);
        }

        public Task<Stream?> OpenJobInputAsync(int id, CancellationToken cancellationToken)
        {
            return Task.FromResult(StoredInput);
        }

        public void RemoveJobInput(int id)
        {
            throw new NotSupportedException();
        }

        public Task<ImportLog?> ClaimNextPendingJobAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(NextJob);
        }

        public Task ReleaseJobAsync(int id, CancellationToken cancellationToken)
        {
            ReleasedJobId = id;
            ReleaseToken = cancellationToken;

            return ReleaseFailure is null ? Task.CompletedTask : Task.FromException(ReleaseFailure);
        }

        public Task MarkJobFailedAsync(int id, string reason, CancellationToken cancellationToken)
        {
            FailedJob = (id, reason);

            return Task.CompletedTask;
        }

        public Task<InterruptedJobRecovery> RecoverInterruptedJobsAsync(
            int maxAttempts,
            string exhaustedReason,
            CancellationToken cancellationToken)
        {
            RecoveryMaxAttempts = maxAttempts;
            RecoveryReason = exhaustedReason;

            return Task.FromResult(RecoveryResult);
        }
    }

    private sealed class StubImportService(Func<CancellationToken, Task<ImportResult>> import)
        : ICustomerImportService
    {
        public CancellationToken? ReceivedToken { get; private set; }

        public Stream? ReceivedInput { get; private set; }

        public Task<ImportResult> ImportAsync(
            ImportLog job,
            Stream input,
            CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            ReceivedInput = input;

            return import(cancellationToken);
        }
    }

    private static ImportLog CreateClaimedJob()
    {
        return new ImportLog
        {
            Id = 42,
            Status = ImportJobStatus.Processing,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            AttemptCount = 1
        };
    }

    private CustomerImportJobRunner CreateRunner(ICustomerImportService importService)
    {
        return new CustomerImportJobRunner(
            _repository,
            importService,
            Options.Create(new CustomerImportWorkerOptions { MaxAttempts = 3 }),
            _logger);
    }

    private CustomerImportJobRunner CreateRunnerThrowing(Exception exception)
    {
        _repository.NextJob = CreateClaimedJob();

        return CreateRunner(new StubImportService(_ => Task.FromException<ImportResult>(exception)));
    }

    [Fact]
    public async Task RunNextJob_WithNoJob_ReturnsFalseWithoutImporting()
    {
        var importService = new StubImportService(_ => throw new InvalidOperationException());

        Assert.False(await CreateRunner(importService).RunNextJobAsync(CancellationToken.None));
        Assert.Null(importService.ReceivedToken);
    }

    [Fact]
    public async Task RunNextJob_PassesStoppingTokenToImportAndChangesNothingOnSuccess()
    {
        _repository.NextJob = CreateClaimedJob();

        var importService = new StubImportService(_ => Task.FromResult(new ImportResult()));

        using var stopping = new CancellationTokenSource();

        Assert.True(await CreateRunner(importService).RunNextJobAsync(stopping.Token));

        Assert.Equal(stopping.Token, importService.ReceivedToken);
        Assert.Same(_repository.StoredInput, importService.ReceivedInput);
        Assert.Null(_repository.FailedJob);
        Assert.Null(_repository.ReleasedJobId);
    }

    [Fact]
    public async Task RunNextJob_WithNoStoredInput_FailsExplicitlyWithoutImporting()
    {
        _repository.NextJob = CreateClaimedJob();
        _repository.StoredInput = null;

        var importService = new StubImportService(_ => throw new InvalidOperationException());

        Assert.True(await CreateRunner(importService).RunNextJobAsync(CancellationToken.None));

        Assert.Null(importService.ReceivedToken);
        Assert.Equal(
            (42, "The import job has no stored input, so it could not run. Submit a new import. No customers were changed."),
            _repository.FailedJob);
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
    }

    // The reason is shown to clients, while the exception goes to the logs.
    public static TheoryData<Exception, string> FailuresAndReasons => new()
    {
        { new DbUpdateException(SecretText), "The import could not be saved to the database. No customers were changed." },
        { new DbUpdateConcurrencyException(SecretText), "The import could not be saved to the database. No customers were changed." },
        { new InvalidOperationException(SecretText), "The import failed unexpectedly. No customers were changed." },
        // Not shutting down, so nothing explains this away. It is a server-side timeout.
        { new OperationCanceledException(SecretText), "The import failed unexpectedly. No customers were changed." }
    };

    // A Failed job can have earlier batches applied, and its reason must say so.
    [Fact]
    public async Task RunNextJob_WhenImportFailsAfterCommittedBatches_ReportsWhatWasApplied()
    {
        var runner = CreateRunnerThrowing(new InvalidOperationException(SecretText));

        _repository.PersistedJob = CreateClaimedJob();
        _repository.PersistedJob.ProcessedCount = 2000;
        _repository.PersistedJob.CreatedCount = 2000;

        await runner.RunNextJobAsync(CancellationToken.None);

        Assert.Equal(
            (42, "The import failed unexpectedly. Lines 1 to 2000 were committed and remain applied. Processing stopped at line 2001."),
            _repository.FailedJob);
    }

    // A lost connection can hide a commit that succeeded, so the database decides.
    [Fact]
    public async Task RunNextJob_WhenFailureFollowsACommitThatCompletedTheJob_LeavesItCompleted()
    {
        var runner = CreateRunnerThrowing(new InvalidOperationException(SecretText));

        _repository.PersistedJob = CreateClaimedJob();
        _repository.PersistedJob.Status = ImportJobStatus.Completed;

        Assert.True(await runner.RunNextJobAsync(CancellationToken.None));

        Assert.Null(_repository.FailedJob);
        Assert.Null(_repository.RequeuedJobId);
    }

    [Fact]
    public async Task RunNextJob_WhenAnotherExecutionChangedTheJob_LeavesItAlone()
    {
        var runner = CreateRunnerThrowing(new ImportJobStateChangedException(42));

        Assert.True(await runner.RunNextJobAsync(CancellationToken.None));

        Assert.Null(_repository.FailedJob);
        Assert.Null(_repository.RequeuedJobId);
        Assert.Null(_repository.ReleasedJobId);
    }

    [Theory]
    [MemberData(nameof(FailuresAndReasons))]
    public async Task RunNextJob_WhenImportFails_RecordsSafeReasonAndLogsException(
        Exception exception,
        string expectedReason)
    {
        Assert.True(await CreateRunnerThrowing(exception).RunNextJobAsync(CancellationToken.None));

        Assert.Equal((42, expectedReason), _repository.FailedJob);
        Assert.Null(_repository.ReleasedJobId);

        var entry = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);

        Assert.Same(exception, entry.Exception);
    }

    [Fact]
    public async Task RunNextJob_WhenShutdownCancelsImport_RequeuesInsteadOfFailing()
    {
        using var stopping = new CancellationTokenSource();

        await stopping.CancelAsync();

        var runner = CreateRunnerThrowing(new OperationCanceledException(stopping.Token));

        Assert.True(await runner.RunNextJobAsync(stopping.Token));

        Assert.Equal(42, _repository.ReleasedJobId);
        Assert.Null(_repository.FailedJob);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);

        // Shutdown has already cancelled the stopping token, so requeueing cannot use it.
        Assert.False(_repository.ReleaseToken.IsCancellationRequested);
    }

    // Requeueing is safe, and the real fault must stay visible in the logs.
    [Fact]
    public async Task RunNextJob_WhenGenuineFailureCoincidesWithShutdown_RequeuesAndLogsError()
    {
        using var stopping = new CancellationTokenSource();

        await stopping.CancelAsync();

        var failure = new InvalidOperationException(SecretText);

        Assert.True(await CreateRunnerThrowing(failure).RunNextJobAsync(stopping.Token));

        Assert.Equal(42, _repository.ReleasedJobId);
        Assert.Null(_repository.FailedJob);
        Assert.Same(failure, Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error).Exception);
    }

    // The job stays Processing, which recovery handles at the next startup.
    [Fact]
    public async Task RunNextJob_WhenRequeueFailsDuringShutdown_DoesNotThrow()
    {
        using var stopping = new CancellationTokenSource();

        await stopping.CancelAsync();

        var runner = CreateRunnerThrowing(new OperationCanceledException(stopping.Token));

        _repository.ReleaseFailure = new TimeoutException();

        Assert.True(await runner.RunNextJobAsync(stopping.Token));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Recover_UsesConfiguredAttemptLimitAndSafeReason()
    {
        _repository.RecoveryResult = new InterruptedJobRecovery(Requeued: 1, Failed: 1);

        var runner = CreateRunner(new StubImportService(_ => throw new InvalidOperationException()));

        await runner.RecoverInterruptedJobsAsync(CancellationToken.None);

        Assert.Equal(3, _repository.RecoveryMaxAttempts);
        Assert.Contains("interrupted too many times", _repository.RecoveryReason);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Recover_WithNothingToRecover_DoesNotLogAWarning()
    {
        var runner = CreateRunner(new StubImportService(_ => throw new InvalidOperationException()));

        await runner.RecoverInterruptedJobsAsync(CancellationToken.None);

        Assert.Empty(_logger.Entries);
    }
}
