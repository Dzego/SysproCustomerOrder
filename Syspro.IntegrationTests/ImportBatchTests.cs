using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.ExceptionHandling;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Syspro.IntegrationTests;

public sealed class ImportBatchTests : SqlServerTestBase
{
    private const int BatchSize = 3;
    private const int MaxAttempts = 3;

    private readonly ITestOutputHelper _output;

    public ImportBatchTests(SqlServerFixture fixture, ITestOutputHelper output)
        : base(fixture)
    {
        _output = output;
    }

    private static string Valid(int i, string name = "Customer")
    {
        return TestData.LegacyLine(legacyCustomerId: $"{i:D10}", name: $"{name} {i}");
    }

    private static string Invalid(int i)
    {
        return TestData.LegacyLine(legacyCustomerId: $"{i:D10}", name: $"Bad {i}", tier: "Z");
    }

    private CustomerImportJobRunner CreateRunner(AppDbContext dbContext, int batchSize = BatchSize)
    {
        var repository = new ImportRepository(dbContext);

        return new CustomerImportJobRunner(
            repository,
            CreateService(dbContext, batchSize),
            Options.Create(new CustomerImportWorkerOptions { MaxAttempts = MaxAttempts }),
            TestData.ImportOptions(batchSize),
            new TestOutputLogger<CustomerImportJobRunner>(_output));
    }

    private static CustomerImportService CreateService(AppDbContext dbContext, int batchSize = BatchSize)
    {
        return new CustomerImportService(
            new LegacyCustomerParser(),
            new CustomerRepository(dbContext),
            new ImportRepository(dbContext),
            TestData.ImportOptions(batchSize));
    }

    private async Task<bool> RunNextJobAsync(int batchSize = BatchSize, params IInterceptor[] interceptors)
    {
        await using var dbContext = CreateDbContext(interceptors);

        return await CreateRunner(dbContext, batchSize).RunNextJobAsync(CancellationToken.None);
    }

    private async Task RunUntilIdleAsync(int batchSize = BatchSize)
    {
        await ExpireLeasesAsync();

        while (await RunNextJobAsync(batchSize))
        {
        }
    }

    // Runs the service without the runner, so a failure leaves the row as a crash would.
    private async Task<Exception> RunUntilProcessDiesAsync(int jobBatchSize, params IInterceptor[] interceptors)
    {
        await using var dbContext = CreateDbContext(interceptors);

        var repository = new ImportRepository(dbContext);

        var job = await repository.ClaimNextJobAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);

        await using var input = await repository.OpenJobInputAsync(job!.Id, CancellationToken.None);

        return await Assert.ThrowsAnyAsync<Exception>(
            () => CreateService(dbContext, jobBatchSize).ImportAsync(job, input!, CancellationToken.None));
    }

    private Task<ImportLog> GetJobAsync(int id)
    {
        return QueryAsync(context => context.ImportLogs.SingleAsync(x => x.Id == id));
    }

    private Task<bool> HasInputAsync(int id)
    {
        return QueryAsync(context => context.ImportJobInputs.AnyAsync(x => x.ImportLogId == id));
    }

    private sealed record Outcome(
        ImportJobStatus Status,
        int Processed,
        int Created,
        int Updated,
        int Failed,
        string Customers,
        string Errors);

    private async Task<Outcome> ReadOutcomeAsync(int jobId)
    {
        var job = await GetJobAsync(jobId);

        var customers = await QueryAsync(context => context.Customers
            .OrderBy(x => x.LegacyCustomerId)
            .Select(x => x.LegacyCustomerId + "=" + x.Name + "/" + x.Tier)
            .ToListAsync());

        var errors = await QueryAsync(context => context.ImportErrors
            .Where(x => x.ImportLogId == jobId)
            .OrderBy(x => x.LineNumber)
            .Select(x => x.LineNumber + "=" + x.RawData + "=" + x.Reason)
            .ToListAsync());

        return new Outcome(
            job.Status,
            job.ProcessedCount,
            job.CreatedCount,
            job.UpdatedCount,
            job.FailedCount,
            string.Join("\n", customers),
            string.Join("\n", errors));
    }

    // What an uninterrupted run of these lines produces, leaving the database empty again.
    private async Task<Outcome> RunControlAsync(params string[] lines)
    {
        var job = await AddPendingJobAsync(input: TestData.ToInput(lines));

        Assert.True(await RunNextJobAsync());

        var outcome = await ReadOutcomeAsync(job.Id);

        await using var dbContext = CreateDbContext();

        await dbContext.ImportLogs.ExecuteDeleteAsync();
        await dbContext.Customers.ExecuteDeleteAsync();

        return outcome;
    }

    // A mix that exercises every counter and spreads across several batches of three.
    private static string[] MixedLines()
    {
        return
        [
            Valid(1),
            Invalid(2),
            Valid(3),
            Valid(4),
            Valid(1, "Duplicate"),
            Invalid(6),
            Valid(7),
            string.Empty,
            Valid(9)
        ];
    }

    // ---------- Batch boundaries ----------

    [SqlServerTheory]
    [InlineData(0, 1)]
    [InlineData(BatchSize - 1, 1)]
    [InlineData(BatchSize, 1)]
    [InlineData(BatchSize + 1, 2)]
    [InlineData(BatchSize * 2, 2)]
    [InlineData(BatchSize * 2 + 1, 3)]
    [InlineData(BatchSize * 3, 3)]
    public async Task Import_CommitsOneTransactionPerBatchAndEveryLine(int lineCount, int expectedCommits)
    {
        var lines = Enumerable.Range(1, lineCount).Select(i => Valid(i)).ToArray();

        var pending = await AddPendingJobAsync(input: TestData.ToInput(lines));

        var counter = new SaveAndCommitCounter();

        Assert.True(await RunNextJobAsync(BatchSize, counter));

        Assert.Equal(expectedCommits, counter.CommitCount);

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(lineCount, job.ProcessedCount);
        Assert.Equal(lineCount, job.CreatedCount);
        Assert.Equal(0, job.ConsecutiveFailureCount);
        Assert.NotNull(job.CompletedAt);
        Assert.Equal(lineCount, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.False(await HasInputAsync(pending.Id));
    }

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(1000)]
    public async Task EveryBatchSize_ProducesTheSameOutcome(int batchSize)
    {
        var control = await RunControlAsync(MixedLines());

        var job = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        Assert.True(await RunNextJobAsync(batchSize));

        Assert.Equal(control, await ReadOutcomeAsync(job.Id));
    }

    [SqlServerFact]
    public async Task Import_InsertsAndUpdatesAcrossBatches()
    {
        var existing = await AddCustomerAsync($"{5:D10}", "Old 5");
        var alsoExisting = await AddCustomerAsync($"{2:D10}", "Old 2");

        var lines = Enumerable.Range(1, 7).Select(i => Valid(i, "New")).ToArray();

        var pending = await AddPendingJobAsync(input: TestData.ToInput(lines));

        await RunNextJobAsync();

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(5, job.CreatedCount);
        Assert.Equal(2, job.UpdatedCount);
        Assert.Equal(7, job.ProcessedCount);
        Assert.Equal(7, await QueryAsync(context => context.Customers.CountAsync()));

        var updated = await QueryAsync(context => context.Customers.SingleAsync(x => x.Id == existing.Id));

        Assert.Equal("New 5", updated.Name);
        Assert.Equal(
            "New 2",
            (await QueryAsync(context => context.Customers.SingleAsync(x => x.Id == alsoExisting.Id))).Name);
    }

    [SqlServerFact]
    public async Task Import_KeepsChangeTrackingToOneBatch()
    {
        var lines = Enumerable.Range(1, 300).Select(i => i % 2 == 0 ? Valid(i) : Invalid(i)).ToArray();

        await AddPendingJobAsync(input: TestData.ToInput(lines));

        var recorder = new TrackedEntityRecorder();

        Assert.True(await RunNextJobAsync(50, recorder));

        Assert.InRange(recorder.MaxTracked, 1, 50);
    }

    // Enough rows to show a real run in the test output, and to check the batching holds.
    [SqlServerFact]
    public async Task Import_LargeFile_CompletesInBatches()
    {
        const int rows = 50_000;

        var builder = new StringBuilder(rows * 81);

        for (var i = 1; i <= rows; i++)
        {
            builder.Append(i % 50 == 0 ? Invalid(i) : Valid(i)).Append('\n');
        }

        var pending = await AddPendingJobAsync(input: Encoding.UTF8.GetBytes(builder.ToString()));

        var recorder = new TrackedEntityRecorder();
        var counter = new SaveAndCommitCounter();
        var started = System.Diagnostics.Stopwatch.StartNew();

        await RunNextJobAsync(1000, recorder, counter);

        started.Stop();

        _output.WriteLine($"Imported {rows} rows in {started.ElapsedMilliseconds} ms, {counter.CommitCount} commits.");

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(49_000, job.CreatedCount);
        Assert.Equal(1_000, job.FailedCount);
        Assert.Equal(50, counter.CommitCount);
        Assert.InRange(recorder.MaxTracked, 1, 1000);
    }

    // ---------- Duplicates across batches ----------

    [SqlServerFact]
    public async Task DuplicateIds_SpanningBatches_KeepTheFirstAndRecordTheRest()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(
            TestData.LegacyLine(legacyCustomerId: "ABC123", name: "First"),
            Valid(2),
            Valid(3),
            TestData.LegacyLine(legacyCustomerId: "abc123", name: "Case variant"),
            Valid(5),
            Valid(6),
            TestData.LegacyLine(legacyCustomerId: "ABC123", name: "Exact duplicate")));

        await RunNextJobAsync();

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(7, job.ProcessedCount);
        Assert.Equal(5, job.CreatedCount);
        Assert.Equal(2, job.FailedCount);

        var customer = await QueryAsync(context =>
            context.Customers.SingleAsync(x => x.LegacyCustomerId == "ABC123"));

        Assert.Equal("First", customer.Name);

        var errors = await QueryAsync(context =>
            context.ImportErrors.OrderBy(x => x.LineNumber).ToListAsync());

        Assert.Equal(new[] { 4, 7 }, errors.Select(x => x.LineNumber));
        Assert.Equal("Duplicate LegacyCustomerId abc123. First seen on line 1.", errors[0].Reason);
        Assert.Equal("Duplicate LegacyCustomerId ABC123. First seen on line 1.", errors[1].Reason);
    }

    // An invalid row is not the first occurrence, even in an earlier batch.
    [SqlServerFact]
    public async Task FirstValidOccurrence_CanBeInALaterBatchThanAnInvalidOne()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(
            TestData.LegacyLine(legacyCustomerId: "0000000001", name: "Invalid", tier: "Z"),
            Valid(2),
            Valid(3),
            TestData.LegacyLine(legacyCustomerId: "0000000001", name: "First valid"),
            Valid(5),
            Valid(6),
            TestData.LegacyLine(legacyCustomerId: "0000000001", name: "Duplicate")));

        await RunNextJobAsync();

        Assert.Equal(
            "First valid",
            (await QueryAsync(context => context.Customers.SingleAsync(x => x.LegacyCustomerId == "0000000001"))).Name);

        var reason = await QueryAsync(context => context.ImportErrors
            .Where(x => x.LineNumber == 7)
            .Select(x => x.Reason)
            .SingleAsync());

        Assert.Equal("Duplicate LegacyCustomerId 0000000001. First seen on line 4.", reason);
        Assert.Equal(5, (await GetJobAsync(pending.Id)).CreatedCount);
    }

    // The duplicate check is rebuilt from the snapshot, because it lives only in memory.
    [SqlServerFact]
    public async Task DuplicateAfterARestart_IsStillDetected()
    {
        var lines = new[]
        {
            TestData.LegacyLine(legacyCustomerId: "0000000001", name: "First"),
            Valid(2),
            Valid(3),
            Valid(4),
            Valid(5),
            Valid(6),
            TestData.LegacyLine(legacyCustomerId: "0000000001", name: "Duplicate")
        };

        var control = await RunControlAsync(lines);

        var pending = await AddPendingJobAsync(input: TestData.ToInput(lines));

        await RunUntilProcessDiesAsync(BatchSize, new ThrowOnSaveInterceptor(2));

        Assert.Equal(BatchSize, (await GetJobAsync(pending.Id)).ProcessedCount);

        await RunUntilIdleAsync();

        Assert.Equal(control, await ReadOutcomeAsync(pending.Id));
    }

    // ---------- Legacy customer ID safety ----------

    [SqlServerFact]
    public async Task UnsupportedId_IsARowErrorAndCannotUpdateAnUnrelatedCustomer()
    {
        // This is what a non-Latin ID already looks like after being stored as varchar.
        await using (var dbContext = CreateDbContext())
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"INSERT INTO [Customers] ([LegacyCustomerId], [Name], [Email], [Tier], [SignupDate]) VALUES ('??', 'Unrelated', 'u@example.com', 'A', '2021-01-01')");
        }

        var pending = await AddPendingJobAsync(input: TestData.ToInput(
            TestData.LegacyLine(legacyCustomerId: "日本", name: "Attacker"),
            TestData.LegacyLine(legacyCustomerId: "中国", name: "Attacker"),
            Valid(3)));

        await RunNextJobAsync();

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(3, job.ProcessedCount);
        Assert.Equal(1, job.CreatedCount);
        Assert.Equal(0, job.UpdatedCount);
        Assert.Equal(2, job.FailedCount);

        var unrelated = await QueryAsync(context =>
            context.Customers.SingleAsync(x => x.LegacyCustomerId == "??"));

        Assert.Equal("Unrelated", unrelated.Name);

        var reasons = await QueryAsync(context => context.ImportErrors.Select(x => x.Reason).Distinct().ToListAsync());

        Assert.Equal(
            "Invalid LegacyCustomerId: only printable ASCII characters are allowed.",
            Assert.Single(reasons));
    }

    [SqlServerFact]
    public async Task ShortAndPunctuatedIds_AreStillImported()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(
            TestData.LegacyLine(legacyCustomerId: "1"),
            TestData.LegacyLine(legacyCustomerId: "A-1_b/2!?~"),
            TestData.LegacyLine(legacyCustomerId: "1234567890"),
            TestData.LegacyLine(legacyCustomerId: "   ")));

        await RunNextJobAsync();

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(3, job.CreatedCount);
        Assert.Equal(1, job.FailedCount);

        var ids = await QueryAsync(context =>
            context.Customers.OrderBy(x => x.LegacyCustomerId).Select(x => x.LegacyCustomerId).ToListAsync());

        Assert.Equal(new[] { "1", "1234567890", "A-1_b/2!?~" }.Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
    }

    // ---------- Failure during a batch ----------

    [SqlServerFact]
    public async Task FailureInBatchThree_RollsBackOnlyThatBatch()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        await RunUntilProcessDiesAsync(BatchSize, new ThrowOnSaveInterceptor(3));

        var job = await GetJobAsync(pending.Id);

        // Batches 1 and 2 stay: six lines, with two row errors.
        Assert.Equal(ImportJobStatus.Processing, job.Status);
        Assert.Equal(6, job.ProcessedCount);
        Assert.Equal(3, job.CreatedCount);
        Assert.Equal(3, job.FailedCount);

        var ids = await QueryAsync(context =>
            context.Customers.OrderBy(x => x.LegacyCustomerId).Select(x => x.LegacyCustomerId).ToListAsync());

        Assert.Equal(new[] { $"{1:D10}", $"{3:D10}", $"{4:D10}" }, ids);
        Assert.Equal(new[] { 2, 5, 6 }, await QueryAsync(context =>
            context.ImportErrors.OrderBy(x => x.LineNumber).Select(x => x.LineNumber).ToListAsync()));

        Assert.True(await HasInputAsync(pending.Id));
    }

    // Fails partway through the batch's statements, after its customers were written.
    [SqlServerFact]
    public async Task StatementFailureInsideABatch_RollsBackWhatThatBatchAlreadyWrote()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(
            Valid(1),
            Invalid(2),
            Valid(3),
            Valid(4),
            Invalid(5),
            Valid(6)));

        var exception = await RunUntilProcessDiesAsync(
            BatchSize,
            new FailCommandInterceptor("[ImportErrors]", occurrence: 2));

        // EF Core wraps failures that happen during a save.
        Assert.IsType<FailCommandInterceptor.CommandFailedException>(exception.InnerException);

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(3, job.ProcessedCount);
        Assert.Equal(2, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.Equal(1, await QueryAsync(context => context.ImportErrors.CountAsync()));
    }

    [SqlServerFact]
    public async Task NonTransientFailure_FailsTheJobAndSaysWhatWasApplied()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        Assert.True(await RunNextJobAsync(BatchSize, new ThrowOnSaveInterceptor(3)));

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal(6, job.ProcessedCount);
        Assert.Equal(
            "The import failed unexpectedly. Lines 1 to 6 were committed and remain applied. Processing stopped at line 7.",
            job.FailureReason);
        Assert.DoesNotContain("hunter2", job.FailureReason);

        // The committed batches are not undone.
        Assert.Equal(3, await QueryAsync(context => context.Customers.CountAsync()));

        // A Failed job is never retried, so its input has no further use.
        Assert.False(await HasInputAsync(pending.Id));
    }

    [SqlServerFact]
    public async Task FailureInTheFirstBatch_SaysNoCustomersChanged()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        Assert.True(await RunNextJobAsync(BatchSize, new ThrowOnSaveInterceptor(1)));

        Assert.Equal(
            "The import failed unexpectedly. No customers were changed.",
            (await GetJobAsync(pending.Id)).FailureReason);
    }

    // ---------- Restart from a checkpoint ----------

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CrashAfterAnyBatch_ResumesToTheSameOutcomeAsAnUninterruptedRun(int failingSave)
    {
        var control = await RunControlAsync(MixedLines());

        var pending = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        await RunUntilProcessDiesAsync(BatchSize, new ThrowOnSaveInterceptor(failingSave));

        await RunUntilIdleAsync();

        Assert.Equal(control, await ReadOutcomeAsync(pending.Id));

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(0, job.ConsecutiveFailureCount);
    }

    // A changed customer proves committed lines are skipped: applying them again would
    // overwrite the change, and would also trip the unique index on their row errors.
    [SqlServerFact]
    public async Task Resume_DoesNotApplyCommittedLinesAgain()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(
            Valid(1),
            Invalid(2),
            Valid(3),
            Valid(4),
            Valid(5)));

        await RunUntilProcessDiesAsync(BatchSize, new ThrowOnSaveInterceptor(2));

        await using (var dbContext = CreateDbContext())
        {
            await dbContext.Customers
                .Where(x => x.LegacyCustomerId == $"{1:D10}")
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Name, "Changed since"));
        }

        await RunUntilIdleAsync();

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(5, job.ProcessedCount);
        Assert.Equal(4, job.CreatedCount);
        Assert.Equal(1, job.FailedCount);
        Assert.Equal(1, await QueryAsync(context => context.ImportErrors.CountAsync()));
        Assert.Equal(
            "Changed since",
            (await QueryAsync(context => context.Customers.SingleAsync(x => x.LegacyCustomerId == $"{1:D10}"))).Name);
    }

    [SqlServerFact]
    public async Task Resume_FromASnapshotCutAcrossChunksAndBatches_PreservesLineNumbers()
    {
        // Larger than a chunk, so that resuming has to read past a chunk boundary.
        var lines = Enumerable.Range(1, 15_000)
            .Select(i => i % 5000 == 0 ? Valid(i) : Invalid(i))
            .ToArray();

        var pending = await AddPendingJobAsync(input: TestData.ToInput(lines));

        await RunUntilProcessDiesAsync(5000, new ThrowOnSaveInterceptor(3));

        Assert.Equal(10_000, (await GetJobAsync(pending.Id)).ProcessedCount);

        await RunUntilIdleAsync(5000);

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(15_000, job.ProcessedCount);
        Assert.Equal(3, job.CreatedCount);

        var errors = await QueryAsync(context => context.ImportErrors
            .OrderBy(x => x.LineNumber)
            .Select(x => new { x.LineNumber, x.RawData })
            .ToListAsync());

        Assert.Equal(14_997, errors.Count);
        Assert.All(errors, error => Assert.Equal(lines[error.LineNumber - 1], error.RawData));
    }

    [SqlServerFact]
    public async Task ApplyBatchProgress_WithAStaleCheckpoint_ChangesNothing()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(Valid(1)));

        await using var dbContext = CreateDbContext();

        var repository = new ImportRepository(dbContext);

        var claim = await repository.ClaimNextJobAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(await repository.ApplyBatchProgressAsync(
            pending.Id, claim!.FencingToken, 0, new ImportBatchProgress(1, 1, 0, 0), completeJob: false, CancellationToken.None));

        Assert.False(await repository.ApplyBatchProgressAsync(
            pending.Id, claim.FencingToken, 0, new ImportBatchProgress(1, 1, 0, 0), completeJob: false, CancellationToken.None));

        Assert.Equal(1, (await GetJobAsync(pending.Id)).ProcessedCount);
    }

    // ---------- Ambiguous commits ----------

    // The commit succeeded, but the worker only sees an error and a dead connection.
    [SqlServerFact]
    public async Task ConnectionLostAfterCommit_DoesNotDoubleCountOrRepeatTheBatch()
    {
        var control = await RunControlAsync(MixedLines());

        var pending = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        Assert.True(await RunNextJobAsync(
            BatchSize,
            new FatalErrorAroundCommitInterceptor(commitNumber: 2, afterCommit: true)));

        var job = await GetJobAsync(pending.Id);

        // It reread the checkpoint, which shows batch two did commit, and requeued.
        Assert.Equal(ImportJobStatus.Pending, job.Status);
        Assert.Equal(6, job.ProcessedCount);
        Assert.True(await HasInputAsync(pending.Id));

        await RunUntilIdleAsync();

        Assert.Equal(control, await ReadOutcomeAsync(pending.Id));
    }

    // The commit that succeeded also completed the job, so there is nothing left to do.
    [SqlServerFact]
    public async Task ConnectionLostAfterTheFinalCommit_LeavesTheJobCompleted()
    {
        var control = await RunControlAsync(MixedLines());

        var pending = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        Assert.True(await RunNextJobAsync(
            BatchSize,
            new FatalErrorAroundCommitInterceptor(commitNumber: 3, afterCommit: true)));

        Assert.Equal(control, await ReadOutcomeAsync(pending.Id));
        Assert.False(await RunNextJobAsync());
        Assert.False(await HasInputAsync(pending.Id));
    }

    [SqlServerFact]
    public async Task ConnectionLostBeforeCommit_RollsBackAndRetriesThatBatch()
    {
        var control = await RunControlAsync(MixedLines());

        var pending = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        Assert.True(await RunNextJobAsync(
            BatchSize,
            new FatalErrorAroundCommitInterceptor(commitNumber: 2, afterCommit: false)));

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Pending, job.Status);
        Assert.Equal(3, job.ProcessedCount);

        await RunUntilIdleAsync();

        Assert.Equal(control, await ReadOutcomeAsync(pending.Id));
    }

    // ---------- Retry tracking ----------

    [SqlServerFact]
    public async Task RepeatedTransientFailureWithoutProgress_FailsAtTheLimit()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(Valid(1)));

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            Assert.True(await RunNextJobAsync(
                BatchSize,
                new FatalErrorAroundCommitInterceptor(commitNumber: 1, afterCommit: false)));

            var job = await GetJobAsync(pending.Id);

            Assert.Equal(attempt, job.AttemptCount);
            Assert.Equal(attempt, job.ConsecutiveFailureCount);
            Assert.Equal(
                attempt < MaxAttempts ? ImportJobStatus.Pending : ImportJobStatus.Failed,
                job.Status);
        }

        Assert.Equal(
            "The import kept failing on a temporary database problem and will not be retried. No customers were changed.",
            (await GetJobAsync(pending.Id)).FailureReason);
        Assert.False(await RunNextJobAsync());
    }

    // More failures than the limit in total, but never in a row, so it must not give up.
    [SqlServerFact]
    public async Task ProgressBetweenFailures_ResetsTheCountSoTheJobFinishes()
    {
        var lines = Enumerable.Range(1, 8).Select(i => Valid(i)).ToArray();

        var pending = await AddPendingJobAsync(input: TestData.ToInput(lines));

        var executions = 0;

        // Each execution commits one batch and then loses its connection on the next.
        while (true)
        {
            executions++;

            Assert.InRange(executions, 1, 20);

            if (!await RunNextJobAsync(
                    1,
                    new FatalErrorAroundCommitInterceptor(commitNumber: 2, afterCommit: false)))
            {
                break;
            }
        }

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(8, job.CreatedCount);
        Assert.True(job.AttemptCount > MaxAttempts, $"Only {job.AttemptCount} attempts were needed.");
        Assert.Equal(0, job.ConsecutiveFailureCount);
    }

    [SqlServerFact]
    public async Task AttemptCount_CountsEveryExecutionAcrossRestarts()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        await RunUntilProcessDiesAsync(BatchSize, new ThrowOnSaveInterceptor(1));
        await RunUntilIdleAsync();

        Assert.Equal(2, (await GetJobAsync(pending.Id)).AttemptCount);
    }

    // ---------- Cancellation and shutdown ----------

    [SqlServerFact]
    public async Task ShutdownBetweenBatches_KeepsTheCheckpointAndIsNotAFailure()
    {
        var control = await RunControlAsync(MixedLines());

        var pending = await AddPendingJobAsync(input: TestData.ToInput(MixedLines()));

        using var shutdown = new CancellationTokenSource();

        await using (var dbContext = CreateDbContext(new CancelOnSaveNumberInterceptor(shutdown, 2)))
        {
            Assert.True(await CreateRunner(dbContext).RunNextJobAsync(shutdown.Token));
        }

        var job = await GetJobAsync(pending.Id);

        Assert.Equal(ImportJobStatus.Pending, job.Status);
        Assert.Equal(3, job.ProcessedCount);
        Assert.Null(job.StartedAt);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(0, job.ConsecutiveFailureCount);
        Assert.Null(job.FailureReason);
        Assert.True(await HasInputAsync(pending.Id));

        await RunUntilIdleAsync();

        Assert.Equal(control, await ReadOutcomeAsync(pending.Id));
        Assert.Equal(2, (await GetJobAsync(pending.Id)).AttemptCount);
    }

    [SqlServerFact]
    public async Task ShutdownBeforeAnyBatchCommits_DoesNotCountAgainstTheRetryLimit()
    {
        var pending = await AddPendingJobAsync(input: TestData.ToInput(Valid(1)));

        for (var shutdowns = 1; shutdowns <= MaxAttempts + 2; shutdowns++)
        {
            using var shutdown = new CancellationTokenSource();

            await using var dbContext = CreateDbContext(new CancelOnSaveNumberInterceptor(shutdown, 1));

            Assert.True(await CreateRunner(dbContext).RunNextJobAsync(shutdown.Token));

            var job = await GetJobAsync(pending.Id);

            Assert.Equal(ImportJobStatus.Pending, job.Status);
            Assert.Equal(shutdowns, job.AttemptCount);
            Assert.Equal(0, job.ConsecutiveFailureCount);
        }
    }

    // ---------- Classifying real failures ----------

    private async Task<SqlException> CaptureAsync(string sql, int? timeoutSeconds = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds ?? command.CommandTimeout;

        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    [SqlServerFact]
    public async Task Classifier_TreatsCommandTimeoutAndLostConnectionAsTransient()
    {
        Assert.True(TransientDatabaseFailureClassifier.IsTransient(
            await CaptureAsync("WAITFOR DELAY '00:00:30'", timeoutSeconds: 1)));

        Assert.True(TransientDatabaseFailureClassifier.IsTransient(
            await CaptureAsync("RAISERROR('Simulated loss of connection', 20, 1) WITH LOG")));
    }

    [SqlServerFact]
    public async Task Classifier_TreatsLockTimeoutAsTransient()
    {
        await using var holder = new SqlConnection(ConnectionString);
        await holder.OpenAsync();
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync();

        await using (var lockRow = holder.CreateCommand())
        {
            lockRow.Transaction = transaction;
            lockRow.CommandText = "EXEC sp_getapplock @Resource = 'stage3', @LockMode = 'Exclusive', @LockOwner = 'Transaction'";
            await lockRow.ExecuteNonQueryAsync();

            lockRow.CommandText = "INSERT INTO [ImportLogs] ([Status], [CreatedAt], [AttemptCount], [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount]) VALUES ('Pending', SYSUTCDATETIME(), 0, 0, 0, 0, 0)";
            await lockRow.ExecuteNonQueryAsync();
        }

        var exception = await CaptureAsync(
            "SET LOCK_TIMEOUT 100; SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; BEGIN TRAN; UPDATE [ImportLogs] SET [AttemptCount] = 1; COMMIT;");

        Assert.Equal(1222, exception.Number);
        Assert.True(TransientDatabaseFailureClassifier.IsTransient(exception));
    }

    [SqlServerFact]
    public async Task Classifier_TreatsDeadlockVictimAsTransient()
    {
        var first = await AddCustomerAsync("0000000001");
        var second = await AddCustomerAsync("0000000002");

        await using var connectionA = new SqlConnection(ConnectionString);
        await using var connectionB = new SqlConnection(ConnectionString);

        await connectionA.OpenAsync();
        await connectionB.OpenAsync();

        await using var transactionA = (SqlTransaction)await connectionA.BeginTransactionAsync();
        await using var transactionB = (SqlTransaction)await connectionB.BeginTransactionAsync();

        Task<int> Update(SqlConnection connection, SqlTransaction transaction, int id)
        {
            var command = connection.CreateCommand();

            command.Transaction = transaction;
            command.CommandText = $"UPDATE [Customers] SET [Name] = 'x' WHERE [Id] = {id}";

            return command.ExecuteNonQueryAsync();
        }

        await Update(connectionA, transactionA, first.Id);
        await Update(connectionB, transactionB, second.Id);

        var results = await Task.WhenAll(
            Capture(Update(connectionA, transactionA, second.Id)),
            Capture(Update(connectionB, transactionB, first.Id)));

        var victim = Assert.Single(results, exception => exception is not null);

        Assert.Equal(1205, victim!.Number);
        Assert.True(TransientDatabaseFailureClassifier.IsTransient(victim));

        static async Task<SqlException?> Capture(Task task)
        {
            try
            {
                await task;

                return null;
            }
            catch (SqlException exception)
            {
                return exception;
            }
        }
    }

    [SqlServerFact]
    public async Task Classifier_DoesNotTreatConstraintOrSyntaxFailuresAsTransient()
    {
        await AddCustomerAsync("0000000001");

        var duplicate = await CaptureAsync(
            "INSERT INTO [Customers] ([LegacyCustomerId], [Name], [Email], [Tier], [SignupDate]) VALUES ('0000000001', 'n', 'e', 'A', '2021-01-01')");

        Assert.Equal(SqlUniqueIndexViolation, duplicate.Number);
        Assert.False(TransientDatabaseFailureClassifier.IsTransient(duplicate));

        Assert.False(TransientDatabaseFailureClassifier.IsTransient(
            await CaptureAsync("SELECT * FROM [TableThatDoesNotExist]")));

        Assert.False(TransientDatabaseFailureClassifier.IsTransient(new InvalidOperationException()));
        Assert.False(TransientDatabaseFailureClassifier.IsTransient(new OperationCanceledException()));
    }

    // ---------- Through the API ----------

    private static async Task<string> CreateContentRootAsync(params string[] lines)
    {
        var directory = Directory.CreateTempSubdirectory();

        Directory.CreateDirectory(Path.Combine(directory.FullName, "LegacyData"));

        await File.WriteAllLinesAsync(
            Path.Combine(directory.FullName, "LegacyData", "customers_legacy.dat"),
            lines);

        return directory.FullName;
    }

    [SqlServerFact]
    public async Task Api_ReportsAPartlyAppliedFailedJobSafelyWithItsCounts()
    {
        var contentRoot = await CreateContentRootAsync(MixedLines());

        try
        {
            using var factory = new ApiFactory(
                ConnectionString,
                contentRoot,
                // The fourth save: one for the POST, then the job's third batch.
                [new ThrowOnSaveInterceptor(4)],
                runImportWorker: true,
                batchSize: BatchSize);
            using var client = factory.CreateClient();

            var response = await client.PostAsync("/api/import/customers", content: null);

            var job = await ImportJobClient.WaitForTerminalStatusAsync(client, response.Headers.Location!);

            Assert.Equal("Failed", job.Status);
            Assert.Equal(6, job.Processed);
            Assert.Equal(3, job.Created);
            Assert.Equal(3, job.Failed);
            Assert.Equal(
                "The import failed unexpectedly. Lines 1 to 6 were committed and remain applied. Processing stopped at line 7.",
                job.FailureReason);

            var body = await client.GetStringAsync(response.Headers.Location);

            Assert.DoesNotContain("hunter2", body);
            Assert.DoesNotContain("InjectedFailure", body);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    // The HTTP contract is unchanged by batching.
    [SqlServerFact]
    public async Task Api_KeepsTheAcceptedAndStatusContractsWhenBatching()
    {
        var contentRoot = await CreateContentRootAsync(MixedLines());

        try
        {
            using var factory = new ApiFactory(
                ConnectionString,
                contentRoot,
                runImportWorker: true,
                batchSize: BatchSize);
            using var client = factory.CreateClient();

            var response = await client.PostAsync("/api/import/customers", content: null);

            Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

            var accepted = await response.Content.ReadFromJsonAsync<ImportJobResponse>();

            Assert.Equal("Pending", accepted!.Status);

            var job = await ImportJobClient.WaitForTerminalStatusAsync(client, response.Headers.Location!);

            Assert.Equal("Completed", job.Status);
            Assert.Equal(9, job.Processed);
            Assert.Equal(5, job.Created);
            Assert.Equal(4, job.Failed);
            Assert.Null(job.FailureReason);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
