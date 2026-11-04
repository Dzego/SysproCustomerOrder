using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.Data;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Syspro.IntegrationTests;

// Calls the runner directly, so that each step happens exactly when a test says.
public sealed class ImportJobPersistenceTests(SqlServerFixture fixture, ITestOutputHelper output) : SqlServerTestBase(fixture)
{
    private const int MaxAttempts = 3;

    private byte[] _input = [];

    private void UseInput(params string[] lines)
    {
        _input = TestData.ToInput(lines);
    }

    private Task<ImportLog> AddJobWithInputAsync(DateTime? createdAt = null)
    {
        return AddPendingJobAsync(createdAt, _input);
    }

    private Task<bool> HasStoredInputAsync(int jobId)
    {
        return QueryAsync(context => context.ImportJobInputs.AnyAsync(x => x.ImportLogId == jobId));
    }

    private CustomerImportJobRunner CreateRunner(AppDbContext dbContext)
    {
        var importRepository = new ImportRepository(dbContext);

        return new CustomerImportJobRunner(
            importRepository,
            new CustomerImportService(
                new LegacyCustomerParser(),
                new CustomerRepository(dbContext),
                importRepository,
            TestData.ImportOptions()),
            Options.Create(new CustomerImportWorkerOptions { MaxAttempts = MaxAttempts }),
            TestData.ImportOptions(),
            new TestOutputLogger<CustomerImportJobRunner>(output));
    }

    private async Task<bool> RunNextJobAsync()
    {
        await using var dbContext = CreateDbContext();

        return await CreateRunner(dbContext).RunNextJobAsync(CancellationToken.None);
    }

    private Task<ImportLog> GetJobAsync(int id)
    {
        return QueryAsync(context => context.ImportLogs.SingleAsync(x => x.Id == id));
    }

    // Leaves the row as a process that died during an import would.
    private async Task<ImportLog> AddInterruptedJobAsync(int attemptCount = 1)
    {
        await using var dbContext = CreateDbContext();

        var now = DateTime.UtcNow;

        var job = new ImportLog
        {
            Status = ImportJobStatus.Processing,
            CreatedAt = now,
            StartedAt = now,
            AttemptCount = attemptCount,
            ConsecutiveFailureCount = attemptCount,
            LeaseOwner = "crashed-worker",
            LeaseExpiresAt = now.AddMinutes(-1),
            FencingToken = attemptCount,
            Input = new ImportJobInput { Content = _input }
        };

        dbContext.ImportLogs.Add(job);
        await dbContext.SaveChangesAsync();

        return job;
    }

    // ---------- Successful runs ----------

    [SqlServerFact]
    public async Task RunNextJob_WithNothingPending_ReturnsFalse()
    {
        Assert.False(await RunNextJobAsync());
    }

    [SqlServerFact]
    public async Task RunNextJob_CompletesJobAndPersistsCustomersAndRowErrorsOnIt()
    {
        var invalid = TestData.LegacyLine(legacyCustomerId: "0000000002", tier: "Z");

        UseInput(TestData.LegacyLine(legacyCustomerId: "0000000001"), invalid);

        var pending = await AddJobWithInputAsync();

        Assert.True(await RunNextJobAsync());

        var job = await QueryAsync(context =>
            context.ImportLogs.Include(x => x.Errors).SingleAsync(x => x.Id == pending.Id));

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.FailureReason);
        Assert.Equal(2, job.ProcessedCount);
        Assert.Equal(1, job.CreatedCount);
        Assert.Equal(1, job.FailedCount);

        var error = Assert.Single(job.Errors);

        Assert.Equal(2, error.LineNumber);
        Assert.Equal(invalid, error.RawData);
        Assert.Equal("Invalid tier: Z", error.Reason);

        Assert.Equal(1, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.False(await HasStoredInputAsync(pending.Id));
    }

    [SqlServerFact]
    public async Task RunNextJob_ProcessesOldestJobFirstAndEachOnlyOnce()
    {
        UseInput(TestData.LegacyLine());

        var newer = await AddJobWithInputAsync(new DateTime(2026, 1, 2));
        var older = await AddJobWithInputAsync(new DateTime(2026, 1, 1));

        Assert.True(await RunNextJobAsync());

        Assert.Equal(ImportJobStatus.Completed, (await GetJobAsync(older.Id)).Status);
        Assert.Equal(ImportJobStatus.Pending, (await GetJobAsync(newer.Id)).Status);

        Assert.True(await RunNextJobAsync());
        Assert.False(await RunNextJobAsync());

        var first = await GetJobAsync(older.Id);
        var second = await GetJobAsync(newer.Id);

        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(1, second.UpdatedCount);
        Assert.Equal(1, second.AttemptCount);
    }

    // ---------- Failures ----------

    [SqlServerFact]
    public async Task RunNextJob_WhenSaveFails_FailsWithoutPartialResults()
    {
        UseInput(
            TestData.LegacyLine(legacyCustomerId: "0000000001"),
            TestData.LegacyLine(legacyCustomerId: "0000000002"),
            TestData.LegacyLine(legacyCustomerId: "0000000003", tier: "Z"));

        var pending = await AddJobWithInputAsync();

        await using var dbContext = CreateDbContext(
            new ConcurrentImportInterceptor(ConnectionString, "0000000001"));

        Assert.True(await CreateRunner(dbContext).RunNextJobAsync(CancellationToken.None));

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal(
            "The import could not be saved to the database. No customers were changed.",
            job.FailureReason);
        Assert.Equal(0, job.ProcessedCount);
        Assert.Equal(0, await QueryAsync(context => context.ImportErrors.CountAsync()));

        // A Failed job is never retried, so its input has no further use.
        Assert.False(await HasStoredInputAsync(pending.Id));

        // Only the row committed by the concurrent import exists.
        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal("Concurrent Import", customer.Name);
    }

    // ---------- Restart recovery ----------

    [SqlServerFact]
    public async Task ExpiredLease_IsReclaimedBeforeAnyPendingJob()
    {
        var pending = await AddJobWithInputAsync(new DateTime(2026, 1, 1));
        var interrupted = await AddInterruptedJobAsync();

        await using var dbContext = CreateDbContext();

        var claim = await new ImportRepository(dbContext).ClaimNextJobAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(interrupted.Id, claim!.Id);
        Assert.Equal(2, claim.AttemptCount);
        Assert.True(await HasStoredInputAsync(interrupted.Id));
        Assert.Equal(ImportJobStatus.Pending, (await GetJobAsync(pending.Id)).Status);
    }

    [SqlServerFact]
    public async Task ReclaimAfterExpiry_CompletesInterruptedJobWithoutDuplicates()
    {
        await AddCustomerAsync("0000000001", name: "Original");

        UseInput(
            TestData.LegacyLine(legacyCustomerId: "0000000001", name: "Updated"),
            TestData.LegacyLine(legacyCustomerId: "0000000002"));

        var interrupted = await AddInterruptedJobAsync();

        Assert.True(await RunNextJobAsync());

        var job = await GetJobAsync(interrupted.Id);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(1, job.CreatedCount);
        Assert.Equal(1, job.UpdatedCount);
        Assert.Equal(2, await QueryAsync(context => context.Customers.CountAsync()));
    }

    [SqlServerFact]
    public async Task ReclaimAtTheFailureLimit_FailsInsteadOfRetrying()
    {
        var exhausted = await AddInterruptedJobAsync(attemptCount: MaxAttempts);

        Assert.True(await RunNextJobAsync());

        var failed = await GetJobAsync(exhausted.Id);

        Assert.Equal(ImportJobStatus.Failed, failed.Status);
        Assert.NotNull(failed.CompletedAt);
        Assert.Null(failed.LeaseOwner);
        Assert.Null(failed.LeaseExpiresAt);
        Assert.Contains("interrupted too many times", failed.FailureReason);
        Assert.False(await HasStoredInputAsync(exhausted.Id));
    }

    [SqlServerFact]
    public async Task ReclaimJustBelowTheFailureLimit_StillRuns()
    {
        var retriable = await AddInterruptedJobAsync(attemptCount: MaxAttempts - 1);

        Assert.True(await RunNextJobAsync());

        Assert.Equal(ImportJobStatus.Completed, (await GetJobAsync(retriable.Id)).Status);
    }

    [SqlServerFact]
    public async Task ExpiringLeases_DoesNotChangeFinishedJobs()
    {
        UseInput(TestData.LegacyLine());

        var pending = await AddJobWithInputAsync();

        await RunNextJobAsync();

        var before = await GetJobAsync(pending.Id);

        await ExpireLeasesAsync();

        Assert.False(await RunNextJobAsync());

        var after = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, after.Status);
        Assert.Equal(before.CompletedAt, after.CompletedAt);
    }

    // ---------- Shutdown ----------

    // SQL Server is really executing the save when shutdown cancels it.
    [SqlServerFact]
    public async Task RunNextJob_WhenShutdownInterruptsSave_RequeuesJobAndCommitsNothing()
    {
        UseInput(TestData.LegacyLine());

        var pending = await AddJobWithInputAsync();

        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var shutdown = new CancellationTokenSource();

        await using (var dbContext = CreateDbContext(
            new SlowQueryInterceptor("INSERT INTO [Customers]"),
            new SignalOnQueryInterceptor(saving, "INSERT INTO [Customers]")))
        {
            var run = CreateRunner(dbContext).RunNextJobAsync(shutdown.Token);

            await saving.Task.WaitAsync(TimeSpan.FromSeconds(30));

            shutdown.CancelAfter(TimeSpan.FromMilliseconds(300));

            Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(20)));
        }

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Pending, job.Status);
        Assert.Null(job.StartedAt);
        Assert.Null(job.CompletedAt);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(0, job.ConsecutiveFailureCount);
        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.True(await HasStoredInputAsync(pending.Id));

        // The job is not lost: the next run completes it.
        Assert.True(await RunNextJobAsync());
        Assert.Equal(ImportJobStatus.Completed, (await GetJobAsync(pending.Id)).Status);
    }

    // ---------- Job ownership ----------

    // The other worker claims immediately ahead of this one, whose claim must then find a
    // live lease and take nothing.
    [SqlServerFact]
    public async Task ClaimNextJob_WhenAnotherWorkerClaimsFirst_ReturnsNoJob()
    {
        await AddJobWithInputAsync();

        ImportLog? otherClaim = null;

        var competingClaim = new BeforeFirstUpdateInterceptor(async () =>
        {
            await using var other = CreateDbContext();

            otherClaim = await new ImportRepository(other)
                .ClaimNextJobAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
        });

        await using var dbContext = CreateDbContext(competingClaim);

        var claim = await new ImportRepository(dbContext)
            .ClaimNextJobAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(otherClaim);
        Assert.Null(claim);

        var job = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(ImportJobStatus.Processing, job.Status);
        Assert.Equal(1, job.AttemptCount);
    }

    // A last line of defence, not multi-instance support: if something else changes
    // the job after it was claimed, the import must not commit over that change.
    [SqlServerFact]
    public async Task Import_WhenJobStatusChangedAfterClaim_CommitsNothing()
    {
        UseInput(TestData.LegacyLine());

        await AddJobWithInputAsync();

        await using var dbContext = CreateDbContext();

        var importRepository = new ImportRepository(dbContext);
        var job = await importRepository.ClaimNextJobAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(job);

        await using (var other = CreateDbContext())
        {
            await new ImportRepository(other).MarkJobFailedAsync(job.Id, job.FencingToken, "Taken over.", CancellationToken.None);
        }

        var service = new CustomerImportService(
            new LegacyCustomerParser(),
            new CustomerRepository(dbContext),
            importRepository,
            TestData.ImportOptions());

        var action = () => service.ImportAsync(
            job,
            new MemoryStream(_input),
            CancellationToken.None);

        await Assert.ThrowsAsync<ImportJobStateChangedException>(action);

        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.Equal("Taken over.", (await GetJobAsync(job.Id)).FailureReason);
    }

    // Reachable when shutdown cancels a save that SQL Server had already committed.
    [SqlServerFact]
    public async Task ReleaseAndMarkFailed_DoNotChangeACompletedJob()
    {
        UseInput(TestData.LegacyLine());

        var pending = await AddJobWithInputAsync();

        await RunNextJobAsync();

        await using var dbContext = CreateDbContext();

        var importRepository = new ImportRepository(dbContext);

        var token = (await GetJobAsync(pending.Id)).FencingToken;

        await importRepository.ReleaseJobAsync(pending.Id, token, CancellationToken.None);
        await importRepository.RequeueJobAsync(pending.Id, token, CancellationToken.None);
        await importRepository.MarkJobFailedAsync(pending.Id, token, "Too late.", CancellationToken.None);

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Null(job.FailureReason);
    }

    // ---------- Atomic completion ----------

    private CustomerImportService CreateImportService(AppDbContext dbContext)
    {
        return new CustomerImportService(
            new LegacyCustomerParser(),
            new CustomerRepository(dbContext),
            new ImportRepository(dbContext),
            TestData.ImportOptions());
    }

    private static async Task<ImportLog> ClaimAsync(AppDbContext dbContext)
    {
        var job = await new ImportRepository(dbContext).ClaimNextJobAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(job);

        return job;
    }

    private async Task WriteFileWithEveryKindOfChangeAsync()
    {
        await AddCustomerAsync("0000000001", name: "Original");

        UseInput(
            TestData.LegacyLine(legacyCustomerId: "0000000001", name: "Updated"),
            TestData.LegacyLine(legacyCustomerId: "0000000002", name: "New"),
            TestData.LegacyLine(legacyCustomerId: "0000000003", tier: "Z"));
    }

    [SqlServerFact]
    public async Task Import_WritesEveryKindOfChangeInOneSaveAndOneTransaction()
    {
        await WriteFileWithEveryKindOfChangeAsync();

        var pending = await AddJobWithInputAsync();

        var saves = new SaveAndCommitCounter();

        await using (var dbContext = CreateDbContext(saves))
        {
            var job = await ClaimAsync(dbContext);

            await CreateImportService(dbContext).ImportAsync(job, new MemoryStream(_input), CancellationToken.None);
        }

        Assert.Equal(1, saves.SaveCount);
        Assert.Equal(1, saves.CommitCount);

        var stored = await QueryAsync(context =>
            context.ImportLogs.Include(x => x.Errors).SingleAsync(x => x.Id == pending.Id));

        Assert.Equal(ImportJobStatus.Completed, stored.Status);
        Assert.Single(stored.Errors);
        Assert.False(await HasStoredInputAsync(pending.Id));

        var names = await QueryAsync(context =>
            context.Customers.OrderBy(x => x.LegacyCustomerId).Select(x => x.Name).ToListAsync());

        Assert.Equal(new[] { "Updated", "New" }, names);
    }

    // Every statement has run on the server when this fails, so this is the same as
    // losing the process at the last possible moment before the commit.
    [SqlServerFact]
    public async Task Import_WhenCommitFails_LeavesNoCustomerErrorOrJobChanges()
    {
        await WriteFileWithEveryKindOfChangeAsync();

        var pending = await AddJobWithInputAsync();

        await using (var dbContext = CreateDbContext(new FailOnCommitInterceptor()))
        {
            var job = await ClaimAsync(dbContext);

            await Assert.ThrowsAsync<FailOnCommitInterceptor.CommitFailedException>(
                () => CreateImportService(dbContext).ImportAsync(job, new MemoryStream(_input), CancellationToken.None));
        }

        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal("Original", customer.Name);
        Assert.Equal(0, await QueryAsync(context => context.ImportErrors.CountAsync()));

        var stored = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Processing, stored.Status);
        Assert.Null(stored.CompletedAt);
        Assert.Equal(0, stored.ProcessedCount);
        Assert.True(await HasStoredInputAsync(pending.Id));

        // Which is why recovery can safely run it again.
        await ExpireLeasesAsync();

        Assert.True(await RunNextJobAsync());

        var completed = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, completed.Status);
        Assert.Equal(2, completed.AttemptCount);
        Assert.Equal(1, completed.CreatedCount);
        Assert.Equal(1, completed.UpdatedCount);
        Assert.Equal(1, completed.FailedCount);
        Assert.Equal(1, await QueryAsync(context => context.ImportErrors.CountAsync()));
        Assert.False(await HasStoredInputAsync(pending.Id));
    }

    // ---------- Two workers ----------
    // "Worker A" is running a job while "worker B" looks for work.

    // A live lease protects a running job, however often another worker polls.
    [SqlServerFact]
    public async Task TwoWorkers_ALiveLeaseIsNotReclaimed()
    {
        var pending = await AddJobWithInputAsync();

        await using var workerA = CreateDbContext();

        var job = await ClaimAsync(workerA);

        await using (var workerB = CreateDbContext())
        {
            Assert.Null(await new ImportRepository(workerB).ClaimNextJobAsync(
                "worker-b",
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        }

        var stored = await GetJobAsync(pending.Id);

        Assert.Equal(job.FencingToken, stored.FencingToken);
        Assert.Equal(ImportJobStatus.Processing, stored.Status);
    }

    // Worker A's lease lapsed and worker B took over, so A's work is rejected before it writes.
    [SqlServerFact]
    public async Task TwoWorkers_AReclaimedWorkerIsFencedAndTheNewOwnerFinishes()
    {
        UseInput(TestData.LegacyLine());

        var pending = await AddJobWithInputAsync();

        await using var workerA = CreateDbContext();

        var job = await ClaimAsync(workerA);

        await ExpireLeasesAsync();

        await using (var workerB = CreateDbContext())
        {
            var reclaimed = await ClaimAsync(workerB);

            Assert.True(reclaimed.FencingToken > job.FencingToken);

            await Assert.ThrowsAsync<ImportJobStateChangedException>(
                () => CreateImportService(workerA).ImportAsync(job, new MemoryStream(_input), CancellationToken.None));

            Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));

            await CreateImportService(workerB).ImportAsync(reclaimed, new MemoryStream(_input), CancellationToken.None);
        }

        var completed = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, completed.Status);
        Assert.Equal(2, completed.AttemptCount);
        Assert.Equal(1, await QueryAsync(context => context.Customers.CountAsync()));
    }

    // The attempt limit could otherwise fail a job that is healthy and still running.
    [SqlServerFact]
    public async Task TwoWorkers_AJobAtTheLimitWithALiveLeaseIsNotFailed()
    {
        UseInput(TestData.LegacyLine());

        var interrupted = await AddInterruptedJobAsync(attemptCount: MaxAttempts - 1);

        await using var workerA = CreateDbContext();

        var job = await ClaimAsync(workerA);

        Assert.Equal(MaxAttempts, job.ConsecutiveFailureCount);
        Assert.False(await RunNextJobAsync());

        await CreateImportService(workerA).ImportAsync(job, new MemoryStream(_input), CancellationToken.None);

        var stored = await GetJobAsync(interrupted.Id);

        Assert.Equal(ImportJobStatus.Completed, stored.Status);
        Assert.Null(stored.FailureReason);
    }

    // ---------- Stored input ----------

    [SqlServerFact]
    public async Task ImportJobInput_ForUnknownJob_IsRejected()
    {
        await using var dbContext = CreateDbContext();

        dbContext.ImportJobInputs.Add(new ImportJobInput { ImportLogId = 999, Content = [1] });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());

        Assert.Equal(SqlConstraintViolation, TestData.SqlErrorNumber(exception));
    }

    [SqlServerFact]
    public async Task DeletingJob_RemovesItsStoredInput()
    {
        UseInput(TestData.LegacyLine());

        var pending = await AddJobWithInputAsync();

        await using var dbContext = CreateDbContext();

        await dbContext.ImportLogs.ExecuteDeleteAsync();

        Assert.False(await HasStoredInputAsync(pending.Id));
    }

    // Claiming and job status reads run on every poll, and must not load the file.
    [SqlServerFact]
    public async Task ClaimingAndReadingJobs_DoNotLoadTheStoredInput()
    {
        UseInput(TestData.LegacyLine());

        var pending = await AddJobWithInputAsync();

        var recorder = new CommandTextRecorder();

        await using var dbContext = CreateDbContext(recorder);

        var repository = new ImportRepository(dbContext);

        var claimed = await repository.ClaimNextJobAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
        var read = await repository.GetJobAsync(pending.Id, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Null(claimed.Input);
        Assert.NotNull(read);
        Assert.Null(read.Input);
        Assert.NotEmpty(recorder.Commands);
        Assert.DoesNotContain(recorder.Commands, command => command.Contains("ImportJobInputs"));
    }

    // ---------- Database constraints ----------

    [SqlServerTheory]
    [InlineData("Unknown", "NULL", "NULL", "NULL")]
    [InlineData("Pending", "SYSUTCDATETIME()", "NULL", "NULL")]
    [InlineData("Processing", "NULL", "NULL", "NULL")]
    [InlineData("Processing", "SYSUTCDATETIME()", "SYSUTCDATETIME()", "NULL")]
    [InlineData("Completed", "SYSUTCDATETIME()", "NULL", "NULL")]
    [InlineData("Completed", "SYSUTCDATETIME()", "SYSUTCDATETIME()", "'Reason'")]
    [InlineData("Failed", "SYSUTCDATETIME()", "SYSUTCDATETIME()", "NULL")]
    [InlineData("Failed", "SYSUTCDATETIME()", "NULL", "'Reason'")]
    public async Task InvalidJobState_IsRejectedByTheDatabase(
        string status,
        string startedAt,
        string completedAt,
        string failureReason)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             INSERT INTO [ImportLogs]
                 ([Status], [CreatedAt], [StartedAt], [CompletedAt], [FailureReason], [AttemptCount],
                  [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ('{status}', SYSUTCDATETIME(), {startedAt}, {completedAt}, {failureReason}, 0, 0, 0, 0, 0);
             """;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(SqlConstraintViolation, exception.Number);
    }

    // A job can fail before it ever starts, for example if attempts are exhausted.
    [SqlServerFact]
    public async Task FailedJob_WithoutStartTime_IsAllowed()
    {
        await using var dbContext = CreateDbContext();

        dbContext.ImportLogs.Add(new ImportLog
        {
            Status = ImportJobStatus.Failed,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            FailureReason = "Reason"
        });

        await dbContext.SaveChangesAsync();
    }
}
