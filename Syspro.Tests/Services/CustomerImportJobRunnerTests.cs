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

        public Task<bool> RenewLeaseAsync(
            int id,
            long fencingToken,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> ApplyBatchProgressAsync(
            int id,
            long fencingToken,
            int expectedProcessedCount,
            ImportBatchProgress progress,
            bool completeJob,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ImportLog? PersistedJob { get; set; }

        public int? RequeuedJobId { get; private set; }

        public Task RequeueJobAsync(int id, long fencingToken, CancellationToken cancellationToken)
        {
            RequeuedJobId = id;
            LastToken = fencingToken;

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

        public long? LastToken { get; private set; }

        public string? ClaimOwner { get; private set; }

        public TimeSpan ClaimLease { get; private set; }

        public Task<ImportLog?> ClaimNextJobAsync(string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            ClaimOwner = leaseOwner;
            ClaimLease = leaseDuration;

            return Task.FromResult(NextJob);
        }

        public Task ReleaseJobAsync(int id, long fencingToken, CancellationToken cancellationToken)
        {
            ReleasedJobId = id;
            LastToken = fencingToken;
            ReleaseToken = cancellationToken;

            return ReleaseFailure is null ? Task.CompletedTask : Task.FromException(ReleaseFailure);
        }

        public Task MarkJobFailedAsync(int id, long fencingToken, string reason, CancellationToken cancellationToken)
        {
            FailedJob = (id, reason);
            LastToken = fencingToken;

            return Task.CompletedTask;
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
            FencingToken = 7,
            ConsecutiveFailureCount = 1,
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
            Options.Create(new CustomerImportOptions { LeaseDuration = TimeSpan.FromSeconds(90) }),
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
    public async Task RunNextJob_ClaimsWithTheConfiguredLeaseAndAWorkerName()
    {
        var importService = new StubImportService(_ => Task.FromResult(new ImportResult()));

        await CreateRunner(importService).RunNextJobAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(90), _repository.ClaimLease);
        Assert.False(string.IsNullOrWhiteSpace(_repository.ClaimOwner));
        Assert.InRange(_repository.ClaimOwner!.Length, 1, ImportLog.LeaseOwnerMaxLength);
    }

    // The claim counts itself, so above the limit means MaxAttempts have already been used.
    [Fact]
    public async Task RunNextJob_WhenClaimExceedsTheFailureLimit_FailsWithoutImporting()
    {
        _repository.NextJob = CreateClaimedJob();
        _repository.NextJob.ConsecutiveFailureCount = 4;
        _repository.NextJob.ProcessedCount = 2000;
        _repository.NextJob.CreatedCount = 2000;

        var importService = new StubImportService(_ => throw new InvalidOperationException());

        Assert.True(await CreateRunner(importService).RunNextJobAsync(CancellationToken.None));

        Assert.Null(importService.ReceivedToken);
        Assert.Equal(7, _repository.LastToken);
        Assert.Equal(
            (42, "The import was interrupted too many times and will not be retried. Lines 1 to 2000 were committed and remain applied. Processing stopped at line 2001."),
            _repository.FailedJob);
    }

    [Fact]
    public async Task RunNextJob_AtTheFailureLimit_StillRuns()
    {
        _repository.NextJob = CreateClaimedJob();
        _repository.NextJob.ConsecutiveFailureCount = 3;

        var importService = new StubImportService(_ => Task.FromResult(new ImportResult()));

        await CreateRunner(importService).RunNextJobAsync(CancellationToken.None);

        Assert.NotNull(importService.ReceivedToken);
        Assert.Null(_repository.FailedJob);
    }

    // The new owner's job must not be changed by a worker reporting its own failure.
    [Fact]
    public async Task RunNextJob_WhenFailureFollowsAReclaim_LeavesTheNewOwnersJobAlone()
    {
        var runner = CreateRunnerThrowing(new InvalidOperationException(SecretText));

        _repository.PersistedJob = CreateClaimedJob();
        _repository.PersistedJob.FencingToken = 8;

        Assert.True(await runner.RunNextJobAsync(CancellationToken.None));

        Assert.Null(_repository.FailedJob);
        Assert.Null(_repository.RequeuedJobId);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task RunNextJob_OnFailureAndShutdown_UsesTheClaimsFencingToken()
    {
        await CreateRunnerThrowing(new InvalidOperationException(SecretText)).RunNextJobAsync(CancellationToken.None);

        Assert.Equal(7, _repository.LastToken);

        using var stopping = new CancellationTokenSource();

        await stopping.CancelAsync();

        await CreateRunnerThrowing(new OperationCanceledException(stopping.Token)).RunNextJobAsync(stopping.Token);

        Assert.Equal(42, _repository.ReleasedJobId);
        Assert.Equal(7, _repository.LastToken);
    }
}
