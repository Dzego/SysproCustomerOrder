using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace Syspro.IntegrationTests;

// Runs the real API as separate operating system processes against the test's database, and
// does to them what production does: kill -9, SIGTERM, and freezing a process until its lease
// lapses. Nothing here is simulated or intercepted, so the file is large enough for a job to
// still be running when a test acts. Run alone with:
//   dotnet test --filter "Suite=RealProcess"
[Trait("Suite", "RealProcess")]
public sealed class RealProcessRecoveryTests : SqlServerTestBase
{
    private const int BatchSize = 500;
    private const int Rows = 60_000;
    private const string ImportEndpoint = "/api/import/customers";

    // A step that hangs must fail the test rather than stall the whole run.
    private const int TestTimeoutMilliseconds = 300_000;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(120);

    // SqlClient's default, which the API does not change.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;
    private readonly DirectoryInfo _contentRoot = Directory.CreateTempSubdirectory();
    private readonly List<ApiProcess> _processes = [];

    public RealProcessRecoveryTests(SqlServerFixture fixture, ITestOutputHelper output)
        : base(fixture)
    {
        _output = output;
    }

    public override async Task DisposeAsync()
    {
        foreach (var process in _processes)
        {
            _output.WriteLine($"----- Output of process {process.Name} -----\n{process.Output}");

            await process.DisposeAsync();
        }

        _contentRoot.Delete(recursive: true);

        await base.DisposeAsync();
    }

    private async Task<ApiProcess> StartProcessAsync(string name, TimeSpan? lease = null)
    {
        var process = await ApiProcess.StartAsync(
            name,
            ConnectionString,
            _contentRoot.FullName,
            BatchSize,
            lease ?? TimeSpan.FromSeconds(3));

        _processes.Add(process);

        return process;
    }

    // What a run over the file must end with, whichever path it took.
    private sealed record Expected(string[] Lines, int Created, HashSet<int> ErrorLines, Dictionary<string, string> Names);

    // Every 50th row is invalid, and every 97th repeats the previous ID, so that a retry
    // that repeated any work would show up in the counters, errors or duplicate reasons.
    private async Task<Expected> WriteLargeFileAsync(int rows = Rows)
    {
        var lines = new string[rows];
        var errorLines = new HashSet<int>();
        var names = new Dictionary<string, string>();

        for (var i = 1; i <= rows; i++)
        {
            var id = $"{i:D10}";
            var name = $"Customer {i}";

            if (i % 50 == 0)
            {
                lines[i - 1] = TestData.LegacyLine(legacyCustomerId: id, name: name, tier: "Z");
                errorLines.Add(i);
            }
            else if (i % 97 == 0 && (i - 1) % 50 != 0 && (i - 1) % 97 != 0)
            {
                lines[i - 1] = TestData.LegacyLine(legacyCustomerId: $"{i - 1:D10}", name: "Duplicate");
                errorLines.Add(i);
            }
            else
            {
                lines[i - 1] = TestData.LegacyLine(legacyCustomerId: id, name: name);
                names[id] = name;
            }
        }

        var directory = Directory.CreateDirectory(Path.Combine(_contentRoot.FullName, "LegacyData"));

        await File.WriteAllLinesAsync(Path.Combine(directory.FullName, "customers_legacy.dat"), lines);

        return new Expected(lines, names.Count, errorLines, names);
    }

    private static async Task<int> PostImportAsync(ApiProcess process)
    {
        var response = await process.Client.PostAsync(ImportEndpoint, content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ImportJobResponse>())!.Id;
    }

    private Task<ImportLog> GetJobAsync(int id)
    {
        return QueryAsync(context => context.ImportLogs.AsNoTracking().SingleAsync(x => x.Id == id));
    }

    private async Task<ImportLog> WaitForJobAsync(int id, Func<ImportLog, bool> condition, string description)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (true)
        {
            var job = await GetJobAsync(id);

            if (condition(job))
            {
                return job;
            }

            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {description}. Job: {job.Status}, processed {job.ProcessedCount}, " +
                    $"token {job.FencingToken}, attempts {job.AttemptCount}, owner {job.LeaseOwner}, reason {job.FailureReason}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        }
    }

    private Task<ImportLog> WaitUntilPartWayAsync(int id)
    {
        return WaitForJobAsync(
            id,
            job => job.ProcessedCount >= BatchSize * 2 && job.Status == ImportJobStatus.Processing,
            "the job to commit some batches");
    }

    private Task<ImportLog> WaitUntilCompletedAsync(int id)
    {
        return WaitForJobAsync(
            id,
            job => job.Status is ImportJobStatus.Completed or ImportJobStatus.Failed,
            "the job to finish");
    }

    // What is committed must always agree: counters, customers, row errors and checkpoint.
    private async Task AssertCheckpointIsConsistentAsync(int id)
    {
        var job = await GetJobAsync(id);

        Assert.Equal(0, job.ProcessedCount % BatchSize);
        Assert.Equal(job.ProcessedCount, job.CreatedCount + job.UpdatedCount + job.FailedCount);
        Assert.Equal(job.CreatedCount, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.Equal(job.FailedCount, await QueryAsync(context => context.ImportErrors.CountAsync(x => x.ImportLogId == id)));
        Assert.True(await QueryAsync(context => context.ImportJobInputs.AnyAsync(x => x.ImportLogId == id)));
    }

    private async Task AssertFinishedExactlyOnceAsync(int id, Expected expected)
    {
        var job = await GetJobAsync(id);

        Assert.True(job.Status == ImportJobStatus.Completed, $"{job.Status}: {job.FailureReason}");
        Assert.Equal(expected.Lines.Length, job.ProcessedCount);
        Assert.Equal(expected.Created, job.CreatedCount);
        Assert.Equal(0, job.UpdatedCount);
        Assert.Equal(expected.ErrorLines.Count, job.FailedCount);
        Assert.Equal(0, job.ConsecutiveFailureCount);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Null(job.FailureReason);

        var customers = await QueryAsync(context =>
            context.Customers.Select(x => new { x.LegacyCustomerId, x.Name }).ToListAsync());

        Assert.Equal(expected.Created, customers.Count);
        Assert.Equal(expected.Names.Keys.Order(), customers.Select(x => x.LegacyCustomerId).Order());

        var errors = await QueryAsync(context => context.ImportErrors
            .Where(x => x.ImportLogId == id)
            .Select(x => new { x.LineNumber, x.RawData })
            .ToListAsync());

        Assert.Equal(expected.ErrorLines.Order(), errors.Select(x => x.LineNumber).Order());
        Assert.All(errors, error => Assert.Equal(expected.Lines[error.LineNumber - 1], error.RawData));
        Assert.False(await QueryAsync(context => context.ImportJobInputs.AnyAsync(x => x.ImportLogId == id)));
    }

    private async Task<ApiProcess> FindOwnerAsync(int id, params ApiProcess[] processes)
    {
        var job = await WaitForJobAsync(id, x => x.LeaseOwner is not null, "the job to have an owner");

        // The lease owner is "unique-id:process-id:machine".
        var processId = int.Parse(job.LeaseOwner!.Split(':')[1]);

        return processes.Single(process => process.Id == processId);
    }

    // ---------- Crash and restart ----------

    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task KillDuringImport_RestartResumesFromTheCheckpointWithoutDuplicates()
    {
        var expected = await WriteLargeFileAsync();

        var first = await StartProcessAsync("first");

        var id = await PostImportAsync(first);

        await WaitUntilPartWayAsync(id);

        first.KillNow();

        var afterKill = await GetJobAsync(id);

        // Nothing could run to tidy up, so the job is exactly as the crash left it.
        Assert.Equal(ImportJobStatus.Processing, afterKill.Status);
        Assert.NotNull(afterKill.LeaseOwner);
        Assert.InRange(afterKill.ProcessedCount, BatchSize * 2, Rows - 1);
        await AssertCheckpointIsConsistentAsync(id);

        // A customer from a committed batch, changed since. Reprocessing would overwrite it.
        await using (var dbContext = CreateDbContext())
        {
            await dbContext.Customers
                .Where(x => x.LegacyCustomerId == $"{1:D10}")
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Name, "Changed since the crash"));
        }

        await StartProcessAsync("restarted");

        await WaitUntilCompletedAsync(id);

        await AssertFinishedExactlyOnceAsync(id, expected);

        var job = await GetJobAsync(id);

        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(2, job.FencingToken);
        Assert.Equal(
            "Changed since the crash",
            await QueryAsync(context => context.Customers
                .Where(x => x.LegacyCustomerId == $"{1:D10}")
                .Select(x => x.Name)
                .SingleAsync()));
    }

    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task RepeatedKills_StillFinishBecauseProgressResetsTheFailureCount()
    {
        var expected = await WriteLargeFileAsync();

        var id = 0;
        var checkpoint = 0;

        // More kills than the retry limit allows in a row, each after some progress.
        for (var kill = 1; kill <= 5; kill++)
        {
            var process = await StartProcessAsync($"attempt-{kill}");

            if (kill == 1)
            {
                id = await PostImportAsync(process);
            }

            var reached = checkpoint;

            var job = await WaitForJobAsync(
                id,
                x => x.Status != ImportJobStatus.Processing || x.ProcessedCount >= reached + BatchSize,
                "the job to make progress");

            if (job.Status != ImportJobStatus.Processing)
            {
                break;
            }

            process.KillNow();

            checkpoint = (await GetJobAsync(id)).ProcessedCount;

            await AssertCheckpointIsConsistentAsync(id);

            Assert.True(checkpoint > reached, $"No progress on attempt {kill}: {checkpoint}.");
        }

        await StartProcessAsync("final");

        await WaitUntilCompletedAsync(id);

        await AssertFinishedExactlyOnceAsync(id, expected);
    }

    // ---------- Two independent processes ----------

    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task TwoProcesses_RunEveryJobOnceInOrderWithOneActiveAtATime()
    {
        await WriteLargeFileAsync(rows: 4_000);

        var processA = await StartProcessAsync("a");
        var processB = await StartProcessAsync("b");

        var ids = new List<int>();

        for (var i = 0; i < 6; i++)
        {
            ids.Add(await PostImportAsync(i % 2 == 0 ? processA : processB));
        }

        // Sampled while the jobs run. The index should make more than one impossible.
        var maxActive = 0;

        while (true)
        {
            var statuses = await QueryAsync(context =>
                context.ImportLogs.Select(x => x.Status).ToListAsync());

            maxActive = Math.Max(maxActive, statuses.Count(x => x == ImportJobStatus.Processing));

            if (statuses.All(x => x is ImportJobStatus.Completed or ImportJobStatus.Failed))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        Assert.Equal(1, maxActive);

        var jobs = await QueryAsync(context => context.ImportLogs.OrderBy(x => x.Id).ToListAsync());

        Assert.All(jobs, job => Assert.True(job.Status == ImportJobStatus.Completed, job.FailureReason));
        Assert.All(jobs, job => Assert.Equal(1, job.AttemptCount));
        Assert.All(jobs, job => Assert.Equal(1, job.FencingToken));

        // The first job creates everyone, and each later one updates them.
        Assert.True(jobs[0].CreatedCount > 0);
        Assert.All(jobs.Skip(1), job => Assert.Equal(0, job.CreatedCount));

        // Finished in the order they were accepted.
        var completed = jobs.Select(x => x.CompletedAt!.Value).ToList();

        Assert.Equal(completed.Order(), completed);
    }

    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task KillTheOwner_TheOtherProcessTakesOverAfterTheLeaseExpires()
    {
        var expected = await WriteLargeFileAsync();
        var lease = TimeSpan.FromSeconds(5);

        var processA = await StartProcessAsync("a", lease);
        var processB = await StartProcessAsync("b", lease);

        var id = await PostImportAsync(processA);

        await WaitUntilPartWayAsync(id);

        var owner = await FindOwnerAsync(id, processA, processB);
        var survivor = owner == processA ? processB : processA;

        owner.KillNow();

        var killed = DateTime.UtcNow;

        // The lease was renewed moments ago, so a takeover this soon would be a bug.
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(1, (await GetJobAsync(id)).FencingToken);

        await WaitForJobAsync(id, job => job.FencingToken == 2, "the other process to take over");

        Assert.True(DateTime.UtcNow - killed > TimeSpan.FromSeconds(1));

        await WaitUntilCompletedAsync(id);

        await AssertFinishedExactlyOnceAsync(id, expected);

        Assert.Equal(2, (await GetJobAsync(id)).AttemptCount);
        Assert.False(survivor.HasExited);
    }

    // ---------- Stale workers ----------

    // These freeze a worker at a point confirmed from SQL Server's own view, because the two
    // points behave differently. The lease is long enough that the new owner keeps the job for
    // the rest of the run, so exactly one takeover must happen.

    private enum FreezePoint
    {
        BetweenBatches,
        HoldingTheRowLock
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    // The session of this process that holds the job's row, or 0.
    private Task<int> SessionHoldingTheRowLockAsync(ApiProcess process)
    {
        return ScalarAsync(
            $"""
             SELECT COALESCE(MAX(s.[session_id]), 0)
             FROM sys.dm_tran_locks l
             JOIN sys.dm_exec_sessions s ON s.[session_id] = l.[request_session_id]
             JOIN sys.partitions p ON p.[hobt_id] = l.[resource_associated_entity_id]
             WHERE l.[resource_database_id] = DB_ID()
               AND l.[resource_type] = 'KEY'
               AND l.[request_mode] = 'X'
               AND l.[request_status] = 'GRANT'
               AND p.[object_id] = OBJECT_ID('ImportLogs')
               AND s.[host_process_id] = {process.Id}
             """);
    }

    private Task<int> OpenTransactionsOfAsync(ApiProcess process)
    {
        return ScalarAsync(
            $"""
             SELECT COUNT(*)
             FROM sys.dm_exec_sessions
             WHERE [database_id] = DB_ID() AND [host_process_id] = {process.Id} AND [open_transaction_count] > 0
             """);
    }

    // Stops the worker and looks. If it is not at the wanted point, it runs briefly and tries again.
    private async Task<int> FreezeAtAsync(ApiProcess owner, FreezePoint point, int id)
    {
        for (var tries = 0; tries < 2000; tries++)
        {
            owner.Freeze();

            // A command already on the server finishes without the client's help.
            await Task.Delay(TimeSpan.FromMilliseconds(150));

            var session = await SessionHoldingTheRowLockAsync(owner);

            if (point == FreezePoint.HoldingTheRowLock && session != 0)
            {
                return session;
            }

            if (point == FreezePoint.BetweenBatches && await OpenTransactionsOfAsync(owner) == 0)
            {
                return 0;
            }

            owner.Resume();

            var job = await GetJobAsync(id);

            Assert.True(job.Status == ImportJobStatus.Processing, $"The job ended before the worker froze {point}: {job.Status}.");

            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 90)));
        }

        throw new TimeoutException($"The worker never froze {point}.");
    }

    private Task<int> LeaseMillisecondsLeftAsync(int id)
    {
        return ScalarAsync($"SELECT DATEDIFF(millisecond, SYSUTCDATETIME(), [LeaseExpiresAt]) FROM [ImportLogs] WHERE [Id] = {id}");
    }

    private static int OwnerProcessId(ImportLog job)
    {
        return int.Parse(job.LeaseOwner!.Split(':')[1]);
    }

    // Nothing moves while the worker is frozen, for the whole of the given time.
    private async Task AssertNoTakeoverForAsync(int id, ImportLog frozen, TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < until)
        {
            var job = await GetJobAsync(id);

            Assert.Equal(1, job.FencingToken);
            Assert.Equal(1, job.AttemptCount);
            Assert.Equal(frozen.LeaseOwner, job.LeaseOwner);
            Assert.Equal(frozen.ProcessedCount, job.ProcessedCount);

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    // Proves from SQL Server's own view that the other process is waiting for this lock.
    private async Task WaitUntilClaimIsBlockedAsync(ApiProcess claimer, int holderSession)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (await ScalarAsync(
                   $"""
                    SELECT COUNT(*)
                    FROM sys.dm_exec_requests r
                    JOIN sys.dm_exec_sessions s ON s.[session_id] = r.[session_id]
                    WHERE r.[blocking_session_id] = {holderSession}
                      AND r.[wait_type] LIKE 'LCK%'
                      AND s.[host_process_id] = {claimer.Id}
                    """) == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    // There was exactly one takeover, the work was applied exactly once, and the stale worker
    // left nothing behind. It is awake while this runs, so a write of its own would show here.
    private async Task AssertOneTakeoverAndNoStaleWritesAsync(
        int id,
        Expected expected,
        ApiProcess staleWorker,
        ApiProcess newOwner,
        TimeSpan lease)
    {
        await WaitUntilCompletedAsync(id);

        await AssertFinishedExactlyOnceAsync(id, expected);

        var finished = await GetJobAsync(id);

        Assert.Equal(2, finished.FencingToken);
        Assert.Equal(2, finished.AttemptCount);

        await Task.Delay(lease + TimeSpan.FromSeconds(1));

        Assert.Equal(0, await OpenTransactionsOfAsync(staleWorker));

        await AssertFinishedExactlyOnceAsync(id, expected);

        var later = await GetJobAsync(id);

        Assert.Equal(finished.FencingToken, later.FencingToken);
        Assert.Equal(finished.AttemptCount, later.AttemptCount);
        Assert.Equal(finished.CompletedAt, later.CompletedAt);

        Assert.False(staleWorker.HasExited);
        Assert.False(newOwner.HasExited);
    }

    // With no transaction open, nothing stands in the way once the lease expires, so the other
    // process takes over and keeps working while the original is still frozen.
    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task WorkerFrozenBetweenBatches_IsTakenOverWhileFrozen_AndCannotWriteWhenItWakes()
    {
        // Gaps between batches are short, so this needs a long run to stop in one.
        var expected = await WriteLargeFileAsync(rows: 200_000);
        var lease = TimeSpan.FromSeconds(5);

        var processA = await StartProcessAsync("a", lease);
        var processB = await StartProcessAsync("b", lease);

        var id = await PostImportAsync(processA);

        await WaitUntilPartWayAsync(id);

        var owner = await FindOwnerAsync(id, processA, processB);
        var other = owner == processA ? processB : processA;

        await FreezeAtAsync(owner, FreezePoint.BetweenBatches, id);

        var frozen = await GetJobAsync(id);

        Assert.Equal(1, frozen.FencingToken);

        var reclaimed = await WaitForJobAsync(id, job => job.FencingToken >= 2, "the other process to take over");

        Assert.Equal(other.Id, OwnerProcessId(reclaimed));
        Assert.True(reclaimed.ProcessedCount >= frozen.ProcessedCount);

        // The new owner makes progress while the original has still not run at all.
        var progressed = await WaitForJobAsync(
            id,
            job => job.ProcessedCount > reclaimed.ProcessedCount,
            "the new owner to commit a batch");

        Assert.Equal(ImportJobStatus.Processing, progressed.Status);
        Assert.Equal(2, progressed.FencingToken);

        owner.Resume();

        await AssertOneTakeoverAndNoStaleWritesAsync(id, expected, owner, other, lease);
    }

    // While the frozen worker holds the row, an expired lease cannot be taken over: the claim
    // waits for the lock, which is what stops two writers. Recovery follows the release. The
    // wait covers a whole command timeout, so a claim that started after the expiry is waiting,
    // and that claim's lease must not be used up by the wait.
    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task WorkerFrozenHoldingTheRowLock_BlocksTakeoverUntilItResumes_ThenRecoveryCompletes()
    {
        var expected = await WriteLargeFileAsync();
        var lease = TimeSpan.FromSeconds(5);

        var processA = await StartProcessAsync("a", lease);
        var processB = await StartProcessAsync("b", lease);

        var id = await PostImportAsync(processA);

        await WaitUntilPartWayAsync(id);

        var owner = await FindOwnerAsync(id, processA, processB);
        var other = owner == processA ? processB : processA;

        var holderSession = await FreezeAtAsync(owner, FreezePoint.HoldingTheRowLock, id);

        var frozen = await GetJobAsync(id);

        await AssertNoTakeoverForAsync(id, frozen, lease + CommandTimeout + TimeSpan.FromSeconds(3));

        var left = await LeaseMillisecondsLeftAsync(id);

        Assert.True(left < 0, $"The lease should have expired, but has {left} ms left.");

        await WaitUntilClaimIsBlockedAsync(other, holderSession);

        Assert.Equal(holderSession, await SessionHoldingTheRowLockAsync(owner));

        owner.Resume();

        var reclaimed = await WaitForJobAsync(id, job => job.FencingToken >= 2, "the other process to reclaim the job");

        Assert.Equal(other.Id, OwnerProcessId(reclaimed));

        await AssertOneTakeoverAndNoStaleWritesAsync(id, expected, owner, other, lease);
    }

    // Terminating the frozen worker's SQL session releases the lock without touching the
    // process. It wakes with a batch in progress that no longer exists, and must not be able
    // to commit it after the other process has reclaimed the job.
    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task WorkerFrozenHoldingTheRowLock_WhoseSessionIsKilled_IsTakenOver_AndCannotCommitWhenItWakes()
    {
        var expected = await WriteLargeFileAsync();
        var lease = TimeSpan.FromSeconds(5);

        var processA = await StartProcessAsync("a", lease);
        var processB = await StartProcessAsync("b", lease);

        var id = await PostImportAsync(processA);

        await WaitUntilPartWayAsync(id);

        var owner = await FindOwnerAsync(id, processA, processB);
        var other = owner == processA ? processB : processA;

        var holderSession = await FreezeAtAsync(owner, FreezePoint.HoldingTheRowLock, id);

        var frozen = await GetJobAsync(id);

        await AssertNoTakeoverForAsync(id, frozen, lease + TimeSpan.FromSeconds(1));

        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();

            await using var kill = new SqlCommand($"KILL {holderSession}", connection);

            await kill.ExecuteNonQueryAsync();
        }

        var reclaimed = await WaitForJobAsync(id, job => job.FencingToken >= 2, "the other process to take over");

        Assert.Equal(other.Id, OwnerProcessId(reclaimed));

        // Its batch was rolled back, so the checkpoint is the one from before the freeze.
        Assert.Equal(frozen.ProcessedCount, reclaimed.ProcessedCount);

        owner.Resume();

        await AssertOneTakeoverAndNoStaleWritesAsync(id, expected, owner, other, lease);
    }

    // The troubleshooting advice for a takeover stuck behind a frozen worker: stop that process.
    // Its connection closes, SQL Server rolls back its batch, and the takeover continues.
    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task KillingAWorkerFrozenHoldingTheRowLock_ReleasesItAndTheOtherProcessTakesOver()
    {
        var expected = await WriteLargeFileAsync();
        var lease = TimeSpan.FromSeconds(5);

        var processA = await StartProcessAsync("a", lease);
        var processB = await StartProcessAsync("b", lease);

        var id = await PostImportAsync(processA);

        await WaitUntilPartWayAsync(id);

        var owner = await FindOwnerAsync(id, processA, processB);
        var other = owner == processA ? processB : processA;

        var holderSession = await FreezeAtAsync(owner, FreezePoint.HoldingTheRowLock, id);

        var frozen = await GetJobAsync(id);

        await AssertNoTakeoverForAsync(id, frozen, lease + TimeSpan.FromSeconds(1));

        await WaitUntilClaimIsBlockedAsync(other, holderSession);

        owner.KillNow();

        var reclaimed = await WaitForJobAsync(id, job => job.FencingToken >= 2, "the other process to take over");

        Assert.Equal(other.Id, OwnerProcessId(reclaimed));
        Assert.Equal(frozen.ProcessedCount, reclaimed.ProcessedCount);

        await WaitUntilCompletedAsync(id);

        await AssertFinishedExactlyOnceAsync(id, expected);

        Assert.Equal(2, (await GetJobAsync(id)).FencingToken);
        Assert.False(other.HasExited);
    }

    // ---------- Graceful shutdown ----------

    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task GracefulShutdown_ReleasesTheJobAndRestartContinuesWithoutWaiting()
    {
        var expected = await WriteLargeFileAsync();

        // A lease far longer than this test, so finishing proves the job was released.
        var lease = TimeSpan.FromMinutes(30);

        var first = await StartProcessAsync("first", lease);

        var id = await PostImportAsync(first);

        await WaitUntilPartWayAsync(id);

        await first.StopGracefullyAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, first.ExitCode);

        var stopped = await GetJobAsync(id);

        Assert.Equal(ImportJobStatus.Pending, stopped.Status);
        Assert.Null(stopped.LeaseOwner);
        Assert.Null(stopped.LeaseExpiresAt);
        Assert.Null(stopped.FailureReason);
        Assert.Equal(1, stopped.AttemptCount);

        // The batch that was running rolled back, leaving only whole batches.
        await AssertCheckpointIsConsistentAsync(id);

        await StartProcessAsync("restarted", lease);

        await WaitUntilCompletedAsync(id);

        await AssertFinishedExactlyOnceAsync(id, expected);

        Assert.Equal(2, (await GetJobAsync(id)).AttemptCount);
    }

    // ---------- The whole API ----------

    [SqlServerFact(Timeout = TestTimeoutMilliseconds)]
    public async Task HttpWorkflow_ImportThenCustomersAndOrdersBehaveCorrectly()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_contentRoot.FullName, "LegacyData"));

        await File.WriteAllLinesAsync(
            Path.Combine(directory.FullName, "customers_legacy.dat"),
            [
                TestData.LegacyLine(legacyCustomerId: "0000012345", name: "John Smith"),
                TestData.LegacyLine(legacyCustomerId: "123", name: "Short Id"),
                TestData.LegacyLine(legacyCustomerId: "日本", name: "Unsupported Id"),
                TestData.LegacyLine(legacyCustomerId: "0000000004", signupDate: "20241340"),
                TestData.LegacyLine(legacyCustomerId: "0000012345", name: "Duplicate")
            ]);

        var api = await StartProcessAsync("api");
        var client = api.Client;

        // Import: accepted, then checked at the returned location.
        var accepted = await client.PostAsync(ImportEndpoint, content: null);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.NotNull(accepted.Headers.Location);

        var job = await ImportJobClient.WaitForTerminalStatusAsync(client, accepted.Headers.Location);

        Assert.Equal("Completed", job.Status);
        Assert.Equal(5, job.Processed);
        Assert.Equal(2, job.Created);
        Assert.Equal(3, job.Failed);
        Assert.Null(job.FailureReason);

        // A repeat updates rather than duplicates.
        var again = await client.PostAsync(ImportEndpoint, content: null);

        var repeat = await ImportJobClient.WaitForTerminalStatusAsync(client, again.Headers.Location!);

        Assert.Equal(0, repeat.Created);
        Assert.Equal(2, repeat.Updated);

        // An order for an imported customer, found by its legacy ID.
        var order = new
        {
            legacyCustomerId = "0000012345",
            currency = "zar",
            status = "Created",
            items = new[]
            {
                new { sku = "SKU-001", description = "Keyboard", unitPrice = 19.99m, quantity = 3 }
            }
        };

        var created = await client.PostAsJsonAsync("/api/orders", order);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var retrieved = await client.GetFromJsonAsync<OrderResponse>(created.Headers.Location);

        Assert.Equal("ZAR", retrieved!.Currency);
        Assert.Equal(59.97m, retrieved.Total);

        // Totals include it.
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-5).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(5).ToString("O"));

        var totals = await client.GetFromJsonAsync<List<CustomerOrderTotalResponse>>(
            $"/api/customers/totals?fromDate={from}&toDate={to}");

        Assert.Equal(2, totals!.Count);
        Assert.Equal("0000012345", totals[0].LegacyCustomerId);
        Assert.Equal(59.97m, totals[0].Total);

        // Errors keep their ProblemDetails shape.
        await AssertProblemAsync(await client.GetAsync("/api/orders/999999"), HttpStatusCode.NotFound);
        await AssertProblemAsync(await client.GetAsync("/api/import/jobs/999999"), HttpStatusCode.NotFound);
        await AssertProblemAsync(
            await client.GetAsync($"/api/customers/totals?fromDate={to}&toDate={from}"),
            HttpStatusCode.BadRequest);
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/orders", new { order.legacyCustomerId, order.status, currency = "ZA", order.items }),
            HttpStatusCode.BadRequest,
            expectField: "Currency");
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/orders", new { legacyCustomerId = "9999999999", order.currency, order.status, order.items }),
            HttpStatusCode.NotFound);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string? expectField = null)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((int)expectedStatus, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));

        if (expectField is not null)
        {
            Assert.True(problem.GetProperty("errors").TryGetProperty(expectField, out _));
        }
    }
}
