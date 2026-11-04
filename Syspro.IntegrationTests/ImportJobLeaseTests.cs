using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Syspro.IntegrationTests;

public sealed class ImportJobLeaseTests(SqlServerFixture fixture, ITestOutputHelper output) : SqlServerTestBase(fixture)
{
    private const int BatchSize = 2;
    private const int MaxAttempts = 3;
    private const string PreviousMigration = "20261104185804_AddImportCountersAndRetryTracking";

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static string Valid(int i, string name = "Customer")
    {
        return TestData.LegacyLine(legacyCustomerId: $"{i:D10}", name: $"{name} {i}");
    }

    private static readonly string[] SixLines = Enumerable.Range(1, 6).Select(i => Valid(i)).ToArray();

    private static Task<ImportLog?> ClaimAsync(AppDbContext dbContext, string owner = "worker", TimeSpan? lease = null)
    {
        return new ImportRepository(dbContext).ClaimNextJobAsync(owner, lease ?? Lease, CancellationToken.None);
    }

    private async Task<ImportLog?> ClaimAsync(string owner = "worker", TimeSpan? lease = null)
    {
        await using var dbContext = CreateDbContext();

        return await ClaimAsync(dbContext, owner, lease);
    }

    private static CustomerImportService CreateService(AppDbContext dbContext, TimeSpan? lease = null)
    {
        return new CustomerImportService(
            new LegacyCustomerParser(),
            new CustomerRepository(dbContext),
            new ImportRepository(dbContext),
            TestData.ImportOptions(BatchSize, lease));
    }

    private CustomerImportJobRunner CreateRunner(AppDbContext dbContext)
    {
        return new CustomerImportJobRunner(
            new ImportRepository(dbContext),
            CreateService(dbContext),
            Options.Create(new CustomerImportWorkerOptions { MaxAttempts = MaxAttempts }),
            TestData.ImportOptions(BatchSize),
            new TestOutputLogger<CustomerImportJobRunner>(output));
    }

    private async Task RunJobsUntilIdleAsync()
    {
        while (true)
        {
            await using var dbContext = CreateDbContext();

            if (!await CreateRunner(dbContext).RunNextJobAsync(CancellationToken.None))
            {
                return;
            }
        }
    }

    private Task<ImportLog> GetJobAsync(int id)
    {
        return QueryAsync(context => context.ImportLogs.SingleAsync(x => x.Id == id));
    }

    private async Task<int> ProcessedInDatabaseAsync(int id)
    {
        return (await GetJobAsync(id)).ProcessedCount;
    }

    private async Task AssertAllSixCustomersImportedOnceAsync(int jobId)
    {
        var job = await GetJobAsync(jobId);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(6, job.ProcessedCount);
        Assert.Equal(6, job.CreatedCount);
        Assert.Equal(0, job.FailedCount);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Equal(6, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.False(await QueryAsync(context => context.ImportJobInputs.AnyAsync()));
    }

    // ---------- Atomic claims ----------

    [SqlServerFact]
    public async Task ConcurrentClaims_HaveExactlyOneWinner()
    {
        for (var round = 0; round < 5; round++)
        {
            var job = await AddPendingJobAsync();

            var claims = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(i => ClaimAsync($"worker-{i}")));

            var winner = Assert.Single(claims, claim => claim is not null);

            Assert.Equal(job.Id, winner!.Id);

            var stored = await GetJobAsync(job.Id);

            Assert.Equal(1, stored.AttemptCount);
            Assert.Equal(1, stored.FencingToken);
            Assert.Equal(winner.LeaseOwner, stored.LeaseOwner);

            await using var dbContext = CreateDbContext();

            await new ImportRepository(dbContext).MarkJobFailedAsync(job.Id, winner.FencingToken, "Done", CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task Claim_RecordsOwnerAndTakesTheLeaseFromTheDatabaseClock()
    {
        await AddPendingJobAsync();

        await using var dbContext = CreateDbContext();

        var before = await dbContext.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();

        var claim = await ClaimAsync(dbContext, "worker-a");

        var after = await dbContext.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();

        Assert.NotNull(claim);
        Assert.Equal("worker-a", claim.LeaseOwner);
        Assert.Equal(ImportJobStatus.Processing, claim.Status);
        Assert.InRange(claim.LeaseExpiresAt!.Value, before + Lease, after + Lease);
    }

    [SqlServerFact]
    public async Task EveryClaim_RaisesTheFencingTokenEvenAfterARelease()
    {
        var job = await AddPendingJobAsync();

        await using var dbContext = CreateDbContext();

        var repository = new ImportRepository(dbContext);

        var first = await ClaimAsync(dbContext);

        await repository.ReleaseJobAsync(job.Id, first!.FencingToken, CancellationToken.None);

        var second = await ClaimAsync(dbContext);

        await ExpireLeasesAsync();

        var third = await ClaimAsync(dbContext);

        Assert.Equal(new long[] { 1, 2, 3 }, new[] { first.FencingToken, second!.FencingToken, third!.FencingToken });
    }

    [SqlServerFact]
    public async Task OnlyOneJobIsActive_AndJobsStartInOrder()
    {
        var newer = await AddPendingJobAsync(new DateTime(2026, 1, 2));
        var older = await AddPendingJobAsync(new DateTime(2026, 1, 1));

        var first = await ClaimAsync("worker-a");

        Assert.Equal(older.Id, first!.Id);
        Assert.Null(await ClaimAsync("worker-b"));
        Assert.Equal(ImportJobStatus.Pending, (await GetJobAsync(newer.Id)).Status);

        await using (var dbContext = CreateDbContext())
        {
            await new ImportRepository(dbContext).MarkJobFailedAsync(older.Id, first.FencingToken, "Done", CancellationToken.None);
        }

        Assert.Equal(newer.Id, (await ClaimAsync("worker-b"))!.Id);
    }

    [SqlServerFact]
    public async Task Database_RejectsASecondProcessingJob()
    {
        await AddPendingJobAsync();
        await ClaimAsync();
        await AddPendingJobAsync();

        await using var dbContext = CreateDbContext();

        var exception = await Assert.ThrowsAsync<SqlException>(() => dbContext.Database.ExecuteSqlAsync(
            $"UPDATE [ImportLogs] SET [Status] = 'Processing', [StartedAt] = SYSUTCDATETIME(), [LeaseOwner] = 'x', [LeaseExpiresAt] = SYSUTCDATETIME() WHERE [Status] = 'Pending'"));

        Assert.Equal(SqlUniqueIndexViolation, exception.Number);
    }

    [SqlServerTheory]
    [InlineData("'Processing'", "SYSUTCDATETIME()", "NULL", "'x'")]
    [InlineData("'Processing'", "SYSUTCDATETIME()", "'x'", "NULL")]
    [InlineData("'Pending'", "NULL", "'x'", "NULL")]
    public async Task Database_RejectsLeaseColumnsThatDoNotMatchTheStatus(
        string status,
        string startedAt,
        string owner,
        string expires)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        var expiry = expires == "'x'" ? "SYSUTCDATETIME()" : expires;

        command.CommandText =
            $"""
             INSERT INTO [ImportLogs] ([Status], [CreatedAt], [StartedAt], [LeaseOwner], [LeaseExpiresAt],
                 [AttemptCount], [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ({status}, SYSUTCDATETIME(), {startedAt}, {owner}, {expiry}, 0, 0, 0, 0, 0);
             """;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(SqlConstraintViolation, exception.Number);
        Assert.Contains("CK_ImportLogs_Lease", exception.Message);
    }

    // ---------- Lease expiry and renewal ----------

    [SqlServerFact]
    public async Task LiveLease_CannotBeReclaimed_ExpiredLeaseCan()
    {
        var job = await AddPendingJobAsync();

        var first = await ClaimAsync("worker-a");

        Assert.Null(await ClaimAsync("worker-b"));

        await ExpireLeasesAsync();

        var second = await ClaimAsync("worker-b");

        Assert.Equal(job.Id, second!.Id);
        Assert.Equal("worker-b", second.LeaseOwner);
        Assert.True(second.FencingToken > first!.FencingToken);
    }

    [SqlServerFact]
    public async Task Renewal_ExtendsAnExpiredLeaseThatNobodyReclaimed()
    {
        var job = await AddPendingJobAsync();

        var claim = await ClaimAsync();

        await ExpireLeasesAsync();

        await using (var dbContext = CreateDbContext())
        {
            Assert.True(await new ImportRepository(dbContext)
                .RenewLeaseAsync(job.Id, claim!.FencingToken, Lease, CancellationToken.None));
        }

        // Renewed, so the job is protected again.
        Assert.Null(await ClaimAsync("worker-b"));
        Assert.Equal("worker", (await GetJobAsync(job.Id)).LeaseOwner);
    }

    [SqlServerFact]
    public async Task EachBatch_RenewsTheLease()
    {
        var job = await AddPendingJobAsync(input: TestData.ToInput(SixLines));

        await using var dbContext = CreateDbContext(new AfterCommitInterceptor(1, async () =>
        {
            var stored = await GetJobAsync(job.Id);

            // Started at five minutes, and renewed by the batch to about an hour.
            await using var check = CreateDbContext();

            var remaining = await check.Database
                .SqlQuery<int>($"SELECT DATEDIFF(minute, SYSUTCDATETIME(), [LeaseExpiresAt]) AS [Value] FROM [ImportLogs] WHERE [Id] = {stored.Id}")
                .SingleAsync();

            Assert.InRange(remaining, 58, 60);
        }));

        var claim = await ClaimAsync(dbContext);

        await CreateService(dbContext, TimeSpan.FromHours(1))
            .ImportAsync(claim!, new MemoryStream(TestData.ToInput(SixLines)), CancellationToken.None);

        await AssertAllSixCustomersImportedOnceAsync(job.Id);
    }

    // ---------- Stale workers ----------

    [SqlServerFact]
    public async Task EveryStateChange_IsIgnoredWithAStaleToken()
    {
        var job = await AddPendingJobAsync(input: [1, 2, 3]);

        var stale = await ClaimAsync("stale-worker");

        await ExpireLeasesAsync();

        var current = await ClaimAsync("current-worker");

        await using (var dbContext = CreateDbContext())
        {
            var repository = new ImportRepository(dbContext);
            var token = stale!.FencingToken;

            Assert.False(await repository.RenewLeaseAsync(job.Id, token, Lease, CancellationToken.None));
            Assert.False(await repository.ApplyBatchProgressAsync(
                job.Id, token, 0, new ImportBatchProgress(1, 1, 0, 0), completeJob: true, CancellationToken.None));

            await repository.ReleaseJobAsync(job.Id, token, CancellationToken.None);
            await repository.RequeueJobAsync(job.Id, token, CancellationToken.None);
            await repository.MarkJobFailedAsync(job.Id, token, "Stale", CancellationToken.None);
        }

        var stored = await GetJobAsync(job.Id);

        Assert.Equal(ImportJobStatus.Processing, stored.Status);
        Assert.Equal("current-worker", stored.LeaseOwner);
        Assert.Equal(current!.FencingToken, stored.FencingToken);
        Assert.Null(stored.FailureReason);
        Assert.Equal(0, stored.ProcessedCount);
        Assert.True(await QueryAsync(context => context.ImportJobInputs.AnyAsync()));
    }

    // The new owner takes over between two of the old owner's batches.
    [SqlServerFact]
    public async Task WorkerReclaimedBetweenBatches_StopsAndTheNewOwnerFinishesWithoutDuplicates()
    {
        var input = TestData.ToInput(SixLines);
        var job = await AddPendingJobAsync(input: input);

        ImportLog? reclaimed = null;

        await using (var workerA = CreateDbContext(new AfterCommitInterceptor(1, async () =>
        {
            await ExpireLeasesAsync();

            reclaimed = await ClaimAsync("worker-b");
        })))
        {
            var claim = await ClaimAsync(workerA, "worker-a");

            await Assert.ThrowsAsync<ImportJobStateChangedException>(
                () => CreateService(workerA).ImportAsync(claim!, new MemoryStream(input), CancellationToken.None));
        }

        // Worker A's first batch stays, and it applied nothing after being reclaimed.
        Assert.Equal(BatchSize, await ProcessedInDatabaseAsync(job.Id));
        Assert.Equal(BatchSize, reclaimed!.ProcessedCount);

        await using (var workerB = CreateDbContext())
        {
            await CreateService(workerB).ImportAsync(reclaimed, new MemoryStream(input), CancellationToken.None);
        }

        await AssertAllSixCustomersImportedOnceAsync(job.Id);
    }

    [SqlServerFact]
    public async Task RunnerReclaimedBetweenBatches_LeavesTheNewOwnersJobUntouched()
    {
        var job = await AddPendingJobAsync(input: TestData.ToInput(SixLines));

        ImportLog? reclaimed = null;

        await using (var workerA = CreateDbContext(new AfterCommitInterceptor(1, async () =>
        {
            await ExpireLeasesAsync();

            reclaimed = await ClaimAsync("worker-b");
        })))
        {
            Assert.True(await CreateRunner(workerA).RunNextJobAsync(CancellationToken.None));
        }

        var stored = await GetJobAsync(job.Id);

        Assert.Equal(ImportJobStatus.Processing, stored.Status);
        Assert.Equal("worker-b", stored.LeaseOwner);
        Assert.Equal(reclaimed!.FencingToken, stored.FencingToken);
        Assert.Null(stored.FailureReason);
    }

    // ---------- Both lock orders ----------

    // Stale worker first: its batch holds the row, so the reclaim has to wait for it.
    [SqlServerFact]
    public async Task LockOrder_WorkerHoldsTheRowFirst_ReclaimWaitsThenSeesItsCommit()
    {
        var input = TestData.ToInput(SixLines);
        var job = await AddPendingJobAsync(input: input);

        var pause = new PauseOnSaveInterceptor(saveNumber: 1);

        await using var workerA = CreateDbContext(pause);

        var claim = await ClaimAsync(workerA, "worker-a");

        // A one-millisecond lease, so it has lapsed while the batch is paused.
        var importing = CreateService(workerA, TimeSpan.FromMilliseconds(1))
            .ImportAsync(claim!, new MemoryStream(input), CancellationToken.None);

        await pause.Reached.WaitAsync(TestTimeout);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        var reclaim = ClaimAsync("worker-b");

        // Time for a claim that did not wait to have finished.
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.False(reclaim.IsCompleted, "The reclaim should wait for the batch holding the row.");

        pause.Release();

        var reclaimed = await reclaim.WaitAsync(TestTimeout);

        // Its batch committed legitimately, and the reclaim resumes after it.
        Assert.NotNull(reclaimed);
        Assert.Equal(BatchSize, reclaimed.ProcessedCount);
        Assert.True(reclaimed.FencingToken > claim!.FencingToken);

        // The old worker's next batch is fenced.
        await Assert.ThrowsAsync<ImportJobStateChangedException>(() => importing.WaitAsync(TestTimeout));

        Assert.Equal(BatchSize, await ProcessedInDatabaseAsync(job.Id));

        await using (var workerB = CreateDbContext())
        {
            await CreateService(workerB).ImportAsync(reclaimed, new MemoryStream(input), CancellationToken.None);
        }

        await AssertAllSixCustomersImportedOnceAsync(job.Id);
    }

    // Reclaim first: the stale worker waits for the row, then finds the token has changed.
    [SqlServerFact]
    public async Task LockOrder_ReclaimHoldsTheRowFirst_WorkerWaitsThenIsFenced()
    {
        var job = await AddPendingJobAsync(input: TestData.ToInput(SixLines));

        var stale = await ClaimAsync("worker-a");

        await using var reclaimConnection = new SqlConnection(ConnectionString);
        await reclaimConnection.OpenAsync();
        await using var reclaim = (SqlTransaction)await reclaimConnection.BeginTransactionAsync();

        await using (var command = reclaimConnection.CreateCommand())
        {
            command.Transaction = reclaim;
            command.CommandText =
                $"UPDATE [ImportLogs] SET [FencingToken] = [FencingToken] + 1, [LeaseOwner] = 'worker-b' WHERE [Id] = {job.Id}";

            await command.ExecuteNonQueryAsync();
        }

        await using var workerA = CreateDbContext();

        var fence = new ImportRepository(workerA).RenewLeaseAsync(job.Id, stale!.FencingToken, Lease, CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.False(fence.IsCompleted, "The fence should wait for the reclaim holding the row.");

        await reclaim.CommitAsync();

        Assert.False(await fence.WaitAsync(TestTimeout));
        Assert.Equal("worker-b", (await GetJobAsync(job.Id)).LeaseOwner);
    }

    // Snapshot reads must not let a fence see an old token. It is an update, so it must not.
    [SqlServerFact]
    public async Task TestDatabases_UseSnapshotReads_LikeRealOnes()
    {
        await using var dbContext = CreateDbContext();

        var enabled = await dbContext.Database
            .SqlQuery<bool>($"SELECT [is_read_committed_snapshot_on] AS [Value] FROM sys.databases WHERE [name] = DB_NAME()")
            .SingleAsync();

        Assert.True(enabled);
    }

    // ---------- Retry semantics are preserved ----------

    [SqlServerFact]
    public async Task RepeatedCrashes_WithoutProgress_FailAtTheLimit()
    {
        var job = await AddPendingJobAsync(input: TestData.ToInput(SixLines));

        for (var crash = 1; crash <= MaxAttempts; crash++)
        {
            await ClaimAsync();
            await ExpireLeasesAsync();

            Assert.Equal(crash, (await GetJobAsync(job.Id)).ConsecutiveFailureCount);
        }

        await using (var dbContext = CreateDbContext())
        {
            Assert.True(await CreateRunner(dbContext).RunNextJobAsync(CancellationToken.None));
        }

        var stored = await GetJobAsync(job.Id);

        Assert.Equal(ImportJobStatus.Failed, stored.Status);
        Assert.Contains("interrupted too many times", stored.FailureReason);
        Assert.Contains("No customers were changed", stored.FailureReason);
        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));
    }

    // ---------- Cancellation ----------

    [SqlServerFact]
    public async Task Shutdown_ReleasesTheLeaseAtOnceSoAnotherWorkerNeedNotWait()
    {
        var job = await AddPendingJobAsync(input: TestData.ToInput(SixLines));

        using var shutdown = new CancellationTokenSource();

        await using (var dbContext = CreateDbContext(new CancelOnSaveNumberInterceptor(shutdown, 2)))
        {
            Assert.True(await CreateRunner(dbContext).RunNextJobAsync(shutdown.Token));
        }

        var stored = await GetJobAsync(job.Id);

        Assert.Equal(ImportJobStatus.Pending, stored.Status);
        Assert.Null(stored.LeaseOwner);
        Assert.Null(stored.LeaseExpiresAt);
        Assert.Equal(BatchSize, stored.ProcessedCount);

        await RunJobsUntilIdleAsync();

        await AssertAllSixCustomersImportedOnceAsync(job.Id);
    }

    // A worker reclaimed during its own shutdown must not hand back the new owner's job.
    [SqlServerFact]
    public async Task ShutdownOfAReclaimedWorker_DoesNotReleaseTheNewOwnersJob()
    {
        var job = await AddPendingJobAsync(input: TestData.ToInput(SixLines));

        using var shutdown = new CancellationTokenSource();

        await using (var dbContext = CreateDbContext(new AfterCommitInterceptor(1, async () =>
        {
            await ExpireLeasesAsync();
            await ClaimAsync("worker-b");
            await shutdown.CancelAsync();
        })))
        {
            await CreateRunner(dbContext).RunNextJobAsync(shutdown.Token);
        }

        var stored = await GetJobAsync(job.Id);

        Assert.Equal(ImportJobStatus.Processing, stored.Status);
        Assert.Equal("worker-b", stored.LeaseOwner);
    }

    [SqlServerFact]
    public async Task ClaimWithACancelledToken_ClaimsNothing()
    {
        var job = await AddPendingJobAsync();

        await using var dbContext = CreateDbContext();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ImportRepository(dbContext).ClaimNextJobAsync("worker", Lease, new CancellationToken(canceled: true)));

        Assert.Equal(ImportJobStatus.Pending, (await GetJobAsync(job.Id)).Status);
    }

    // ---------- Real hosted workers ----------

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
    public async Task TwoInstances_ProcessEveryQueuedJobExactlyOnce()
    {
        var contentRoot = await CreateContentRootAsync(SixLines);

        try
        {
            using var instanceA = new ApiFactory(ConnectionString, contentRoot, runImportWorker: true, batchSize: BatchSize);
            using var instanceB = new ApiFactory(ConnectionString, contentRoot, runImportWorker: true, batchSize: BatchSize);
            using var clientA = instanceA.CreateClient();
            using var clientB = instanceB.CreateClient();

            var locations = new List<Uri>();

            for (var i = 0; i < 6; i++)
            {
                var client = i % 2 == 0 ? clientA : clientB;

                var response = await client.PostAsync("/api/import/customers", content: null);

                locations.Add(response.Headers.Location!);
            }

            var results = new List<ImportJobResponse>();

            foreach (var location in locations)
            {
                results.Add(await ImportJobClient.WaitForTerminalStatusAsync(clientA, location));
            }

            Assert.All(results, result => Assert.Equal("Completed", result.Status));
            Assert.Equal(6, results.Sum(result => result.Created));
            Assert.Equal(30, results.Sum(result => result.Updated));
            Assert.Equal(6, await QueryAsync(context => context.Customers.CountAsync()));

            var jobs = await QueryAsync(context => context.ImportLogs.ToListAsync());

            Assert.All(jobs, job => Assert.Equal(1, job.AttemptCount));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    // What a rolling deployment does: a second instance starts while the first is working.
    [SqlServerFact]
    public async Task NewInstance_DoesNotTakeARunningJob_AndContinuesAfterTheOldOneStops()
    {
        const string customerInsert = "INSERT INTO [Customers]";

        var contentRoot = await CreateContentRootAsync(Valid(1));
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            Uri location;
            long tokenWhileRunning;

            using (var oldInstance = new ApiFactory(
                ConnectionString,
                contentRoot,
                [new SlowQueryInterceptor(customerInsert), new SignalOnQueryInterceptor(saving, customerInsert)],
                runImportWorker: true))
            {
                using var oldClient = oldInstance.CreateClient();

                location = (await oldClient.PostAsync("/api/import/customers", content: null)).Headers.Location!;

                await saving.Task.WaitAsync(TestTimeout);

                tokenWhileRunning = (await QueryAsync(context => context.ImportLogs.SingleAsync())).FencingToken;

                using var newInstance = new ApiFactory(ConnectionString, contentRoot, runImportWorker: true);
                using var newClient = newInstance.CreateClient();

                // Many polls of the new worker's 50 ms interval pass here.
                await Task.Delay(TimeSpan.FromSeconds(1));

                var whileRunning = await QueryAsync(context => context.ImportLogs.SingleAsync());

                Assert.Equal(ImportJobStatus.Processing, whileRunning.Status);
                Assert.Equal(tokenWhileRunning, whileRunning.FencingToken);
            }

            // Both instances have stopped, and the graceful one released its job.
            var released = await QueryAsync(context => context.ImportLogs.SingleAsync());

            Assert.Equal(ImportJobStatus.Pending, released.Status);

            using var nextInstance = new ApiFactory(ConnectionString, contentRoot, runImportWorker: true);
            using var nextClient = nextInstance.CreateClient();

            var job = await ImportJobClient.WaitForTerminalStatusAsync(nextClient, location);

            Assert.Equal("Completed", job.Status);
            Assert.Equal(1, job.Created);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    // No recovery step at startup: a new worker waits for the lease, however it got there.
    [SqlServerFact]
    public async Task AfterACrash_ANewInstanceWaitsForTheLeaseToExpire()
    {
        var job = await AddPendingJobAsync(input: TestData.ToInput(SixLines));

        await ClaimAsync("crashed-worker");

        var contentRoot = await CreateContentRootAsync(SixLines);

        try
        {
            using var instance = new ApiFactory(ConnectionString, contentRoot, runImportWorker: true);
            using var client = instance.CreateClient();

            await Task.Delay(TimeSpan.FromSeconds(1));

            var waiting = await GetJobAsync(job.Id);

            Assert.Equal(ImportJobStatus.Processing, waiting.Status);
            Assert.Equal("crashed-worker", waiting.LeaseOwner);

            await ExpireLeasesAsync();

            var result = await ImportJobClient.WaitForTerminalStatusAsync(
                client,
                new Uri($"/api/import/jobs/{job.Id}", UriKind.Relative));

            Assert.Equal("Completed", result.Status);
            Assert.Equal(6, result.Created);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    // ---------- Migration ----------

    private async Task MigrateToAsync(string? target)
    {
        await using var dbContext = CreateDbContext();

        await dbContext.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task ExecuteAsync(FormattableString sql)
    {
        await using var dbContext = CreateDbContext();

        await dbContext.Database.ExecuteSqlAsync(sql);
    }

    private async Task InsertBeforeLeasingAsync(string status, int processed, DateTime? startedAt, DateTime? completedAt)
    {
        var at = new DateTime(2026, 1, 1, 10, 0, 0);

        await ExecuteAsync(
            $"""
             INSERT INTO [ImportLogs]
                 ([Status], [CreatedAt], [StartedAt], [CompletedAt], [AttemptCount], [ConsecutiveFailureCount],
                  [ProcessedCount], [CreatedCount], [UpdatedCount], [FailedCount])
             VALUES ({status}, {at}, {startedAt}, {completedAt}, 2, 1, {processed}, {processed}, 0, 0);
             """);
    }

    [SqlServerFact]
    public async Task Migration_KeepsHistoryAndHasNoLeases()
    {
        var at = new DateTime(2026, 1, 1, 10, 0, 0);

        await MigrateToAsync(PreviousMigration);
        await InsertBeforeLeasingAsync("Completed", 8, at, at);
        await ExecuteAsync(
            $"INSERT INTO [ImportErrors] ([ImportLogId], [LineNumber], [RawData], [Reason]) SELECT [Id], 6, 'r', 'x' FROM [ImportLogs]");

        await MigrateToAsync(null);

        var job = await QueryAsync(context => context.ImportLogs.Include(x => x.Errors).SingleAsync());

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(8, job.ProcessedCount);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(1, job.ConsecutiveFailureCount);
        Assert.Equal(0, job.FencingToken);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Single(job.Errors);
    }

    // Two could exist under the old code, and neither has an owner a lease could fence.
    [SqlServerFact]
    public async Task Migration_ReturnsProcessingJobsToPendingKeepingTheirCheckpoints()
    {
        var at = new DateTime(2026, 1, 1, 10, 0, 0);

        await MigrateToAsync(PreviousMigration);
        await InsertBeforeLeasingAsync("Processing", 2000, at, null);
        await InsertBeforeLeasingAsync("Processing", 3000, at, null);

        await MigrateToAsync(null);

        var jobs = await QueryAsync(context => context.ImportLogs.OrderBy(x => x.Id).ToListAsync());

        Assert.All(jobs, job => Assert.Equal(ImportJobStatus.Pending, job.Status));
        Assert.All(jobs, job => Assert.Null(job.StartedAt));
        Assert.Equal(new[] { 2000, 3000 }, jobs.Select(job => job.ProcessedCount));
    }

    // How the previous version claims, which is what would happen if one were left running.
    [SqlServerFact]
    public async Task AfterMigration_AnOldWorkersClaimIsRejected()
    {
        await AddPendingJobAsync();

        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE [ImportLogs] SET [Status] = 'Processing', [StartedAt] = SYSUTCDATETIME(), [AttemptCount] = [AttemptCount] + 1 WHERE [Status] = 'Pending'"));

        Assert.Equal(SqlConstraintViolation, exception.Number);
        Assert.Contains("CK_ImportLogs_Lease", exception.Message);
    }

    [SqlServerFact]
    public async Task Migration_RevertsAndReappliesKeepingHistory()
    {
        var at = new DateTime(2026, 1, 1, 10, 0, 0);

        await MigrateToAsync(PreviousMigration);
        await InsertBeforeLeasingAsync("Completed", 8, at, at);

        await MigrateToAsync(null);
        await MigrateToAsync(PreviousMigration);

        await using (var dbContext = CreateDbContext())
        {
            var columns = await dbContext.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'ImportLogs' AND COLUMN_NAME IN ('FencingToken', 'LeaseOwner', 'LeaseExpiresAt')")
                .SingleAsync();

            Assert.Equal(0, columns);
        }

        await MigrateToAsync(null);

        Assert.Equal(8, (await QueryAsync(context => context.ImportLogs.SingleAsync())).ProcessedCount);
    }

    [SqlServerFact]
    public async Task FreshDatabase_HasTheLeaseSchema()
    {
        await using var dbContext = CreateDbContext();

        var index = await dbContext.Database
            .SqlQuery<string>($"SELECT CONCAT([is_unique], ':', [has_filter], ':', [filter_definition]) AS [Value] FROM sys.indexes WHERE [name] = 'UX_ImportLogs_SingleProcessing'")
            .SingleAsync();

        Assert.Equal("1:1:([Status]='Processing')", index);

        var pendingChanges = dbContext.Database.HasPendingModelChanges();

        Assert.False(pendingChanges);
    }
}
