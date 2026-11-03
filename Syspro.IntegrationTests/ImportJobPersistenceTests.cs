using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.Data;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

// Calls the runner directly, so that each step happens exactly when a test says.
public sealed class ImportJobPersistenceTests : SqlServerTestBase, IDisposable
{
    private const int MaxAttempts = 3;

    private readonly DirectoryInfo _contentRoot = Directory.CreateTempSubdirectory();

    public ImportJobPersistenceTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    public void Dispose()
    {
        _contentRoot.Delete(recursive: true);
    }

    private async Task WriteLegacyFileAsync(params string[] lines)
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot.FullName, "LegacyData"));

        await File.WriteAllLinesAsync(
            Path.Combine(_contentRoot.FullName, "LegacyData", "customers_legacy.dat"),
            lines);
    }

    private CustomerImportJobRunner CreateRunner(AppDbContext dbContext)
    {
        var importRepository = new ImportRepository(dbContext);

        return new CustomerImportJobRunner(
            importRepository,
            new CustomerImportService(
                new LegacyCustomerParser(),
                new CustomerRepository(dbContext),
                importRepository),
            new TestHostEnvironment(_contentRoot.FullName),
            Options.Create(new CustomerImportWorkerOptions { MaxAttempts = MaxAttempts }),
            NullLogger<CustomerImportJobRunner>.Instance);
    }

    private async Task<bool> RunNextJobAsync()
    {
        await using var dbContext = CreateDbContext();

        return await CreateRunner(dbContext).RunNextJobAsync(CancellationToken.None);
    }

    private async Task RecoverAsync()
    {
        await using var dbContext = CreateDbContext();

        await CreateRunner(dbContext).RecoverInterruptedJobsAsync(CancellationToken.None);
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
            AttemptCount = attemptCount
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

        await WriteLegacyFileAsync(TestData.LegacyLine(legacyCustomerId: "0000000001"), invalid);

        var pending = await AddPendingJobAsync();

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
    }

    [SqlServerFact]
    public async Task RunNextJob_ProcessesOldestJobFirstAndEachOnlyOnce()
    {
        await WriteLegacyFileAsync(TestData.LegacyLine());

        var newer = await AddPendingJobAsync(new DateTime(2026, 1, 2));
        var older = await AddPendingJobAsync(new DateTime(2026, 1, 1));

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
    public async Task RunNextJob_WhenFileIsMissing_FailsWithSafeReasonAndContinues()
    {
        var pending = await AddPendingJobAsync();

        Assert.True(await RunNextJobAsync());

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.NotNull(job.CompletedAt);
        Assert.Equal(
            "The legacy customer file was not found. No customers were changed.",
            job.FailureReason);
        Assert.DoesNotContain(_contentRoot.FullName, job.FailureReason);

        Assert.False(await RunNextJobAsync());
    }

    [SqlServerFact]
    public async Task RunNextJob_WhenSaveFails_FailsWithoutPartialResults()
    {
        await WriteLegacyFileAsync(
            TestData.LegacyLine(legacyCustomerId: "0000000001"),
            TestData.LegacyLine(legacyCustomerId: "0000000002"),
            TestData.LegacyLine(legacyCustomerId: "0000000003", tier: "Z"));

        var pending = await AddPendingJobAsync();

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

        // Only the row committed by the concurrent import exists.
        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal("Concurrent Import", customer.Name);
    }

    // ---------- Restart recovery ----------

    [SqlServerFact]
    public async Task Recover_RequeuesInterruptedJobAndLeavesOthersAlone()
    {
        var interrupted = await AddInterruptedJobAsync();
        var pending = await AddPendingJobAsync();

        await RecoverAsync();

        var recovered = await GetJobAsync(interrupted.Id);

        Assert.Equal(ImportJobStatus.Pending, recovered.Status);
        Assert.Null(recovered.StartedAt);
        Assert.Equal(1, recovered.AttemptCount);
        Assert.Equal(ImportJobStatus.Pending, (await GetJobAsync(pending.Id)).Status);
    }

    [SqlServerFact]
    public async Task Recover_ThenRun_CompletesInterruptedJobWithoutDuplicates()
    {
        await AddCustomerAsync("0000000001", name: "Original");

        await WriteLegacyFileAsync(
            TestData.LegacyLine(legacyCustomerId: "0000000001", name: "Updated"),
            TestData.LegacyLine(legacyCustomerId: "0000000002"));

        var interrupted = await AddInterruptedJobAsync();

        await RecoverAsync();

        Assert.True(await RunNextJobAsync());

        var job = await GetJobAsync(interrupted.Id);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(1, job.CreatedCount);
        Assert.Equal(1, job.UpdatedCount);
        Assert.Equal(2, await QueryAsync(context => context.Customers.CountAsync()));
    }

    [SqlServerFact]
    public async Task Recover_WhenAttemptsAreExhausted_FailsInsteadOfRetrying()
    {
        var exhausted = await AddInterruptedJobAsync(attemptCount: MaxAttempts);
        var retriable = await AddInterruptedJobAsync(attemptCount: MaxAttempts - 1);

        await RecoverAsync();

        var failed = await GetJobAsync(exhausted.Id);

        Assert.Equal(ImportJobStatus.Failed, failed.Status);
        Assert.NotNull(failed.CompletedAt);
        Assert.Contains("interrupted too many times", failed.FailureReason);
        Assert.Equal(ImportJobStatus.Pending, (await GetJobAsync(retriable.Id)).Status);
    }

    [SqlServerFact]
    public async Task Recover_DoesNotChangeFinishedJobs()
    {
        await WriteLegacyFileAsync(TestData.LegacyLine());

        var pending = await AddPendingJobAsync();

        await RunNextJobAsync();

        var before = await GetJobAsync(pending.Id);

        await RecoverAsync();

        var after = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, after.Status);
        Assert.Equal(before.CompletedAt, after.CompletedAt);
    }

    // ---------- Shutdown ----------

    // SQL Server is really executing the save when shutdown cancels it.
    [SqlServerFact]
    public async Task RunNextJob_WhenShutdownInterruptsSave_RequeuesJobAndCommitsNothing()
    {
        await WriteLegacyFileAsync(TestData.LegacyLine());

        var pending = await AddPendingJobAsync();

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
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));

        // The job is not lost: the next run completes it.
        Assert.True(await RunNextJobAsync());
        Assert.Equal(ImportJobStatus.Completed, (await GetJobAsync(pending.Id)).Status);
    }

    // ---------- Job ownership ----------

    // Both workers read the job as Pending, and the other one updates it first. Only
    // the status check in the update stops both from running the same job.
    [SqlServerFact]
    public async Task ClaimNextPendingJob_WhenAnotherWorkerClaimsFirst_ReturnsNoJob()
    {
        await AddPendingJobAsync();

        ImportLog? otherClaim = null;

        var competingClaim = new BeforeFirstUpdateInterceptor(async () =>
        {
            await using var other = CreateDbContext();

            otherClaim = await new ImportRepository(other)
                .ClaimNextPendingJobAsync(CancellationToken.None);
        });

        await using var dbContext = CreateDbContext(competingClaim);

        var claim = await new ImportRepository(dbContext)
            .ClaimNextPendingJobAsync(CancellationToken.None);

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
        await WriteLegacyFileAsync(TestData.LegacyLine());

        await AddPendingJobAsync();

        await using var dbContext = CreateDbContext();

        var importRepository = new ImportRepository(dbContext);
        var job = await importRepository.ClaimNextPendingJobAsync(CancellationToken.None);

        Assert.NotNull(job);

        await using (var other = CreateDbContext())
        {
            await new ImportRepository(other).MarkJobFailedAsync(job.Id, "Taken over.", CancellationToken.None);
        }

        var service = new CustomerImportService(
            new LegacyCustomerParser(),
            new CustomerRepository(dbContext),
            importRepository);

        var action = () => service.ImportAsync(
            job,
            Path.Combine(_contentRoot.FullName, "LegacyData", "customers_legacy.dat"),
            CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(action);

        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.Equal("Taken over.", (await GetJobAsync(job.Id)).FailureReason);
    }

    // Reachable when shutdown cancels a save that SQL Server had already committed.
    [SqlServerFact]
    public async Task ReleaseAndMarkFailed_DoNotChangeACompletedJob()
    {
        await WriteLegacyFileAsync(TestData.LegacyLine());

        var pending = await AddPendingJobAsync();

        await RunNextJobAsync();

        await using var dbContext = CreateDbContext();

        var importRepository = new ImportRepository(dbContext);

        await importRepository.ReleaseJobAsync(pending.Id, CancellationToken.None);
        await importRepository.MarkJobFailedAsync(pending.Id, "Too late.", CancellationToken.None);

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Null(job.FailureReason);
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
