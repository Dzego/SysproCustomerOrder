using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

// An accepted job processes its own stored input, whatever happens to the file.
// Disposing a factory stops its host, which is how these tests simulate a restart.
public sealed class ImportJobInputTests : SqlServerTestBase
{
    private const string ImportEndpoint = "/api/import/customers";
    private const string CustomerInsert = "INSERT INTO [Customers]";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static readonly string AcceptedLine =
        TestData.LegacyLine(legacyCustomerId: "0000000001", name: "Accepted");

    private static readonly string ReplacementLine =
        TestData.LegacyLine(legacyCustomerId: "0000000002", name: "Replacement");

    private readonly DirectoryInfo _contentRoot = Directory.CreateTempSubdirectory();

    public ImportJobInputTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    public override async Task DisposeAsync()
    {
        _contentRoot.Delete(recursive: true);

        await base.DisposeAsync();
    }

    private string LegacyFilePath =>
        Path.Combine(_contentRoot.FullName, "LegacyData", "customers_legacy.dat");

    private async Task WriteLegacyFileAsync(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyFilePath)!);

        await File.WriteAllLinesAsync(LegacyFilePath, lines);
    }

    private ApiFactory CreateFactory(bool runImportWorker = false)
    {
        return new ApiFactory(ConnectionString, _contentRoot.FullName, runImportWorker: runImportWorker);
    }

    // Accepts a job on a host with no worker, then stops that host.
    private async Task<Uri> AcceptJobThenStopAsync()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(ImportEndpoint, content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return response.Headers.Location!;
    }

    private async Task<ImportJobResponse> RunUntilFinishedAsync(Uri location)
    {
        using var factory = CreateFactory(runImportWorker: true);
        using var client = factory.CreateClient();

        return await ImportJobClient.WaitForTerminalStatusAsync(client, location);
    }

    private Task<int> CountInputsAsync()
    {
        return QueryAsync(context => context.ImportJobInputs.CountAsync());
    }

    private async Task AssertOnlyAcceptedCustomerExistsAsync()
    {
        var customer = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal("Accepted", customer.Name);
    }

    private async Task<ImportLog> GetOnlyJobAsync()
    {
        return await QueryAsync(context => context.ImportLogs.SingleAsync());
    }

    // ---------- Capturing the input ----------

    [SqlServerFact]
    public async Task Post_StoresTheExactFileBytesWithThePendingJob()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        await AcceptJobThenStopAsync();

        var input = await QueryAsync(context => context.ImportJobInputs.SingleAsync());
        var job = await GetOnlyJobAsync();

        Assert.Equal(job.Id, input.ImportLogId);
        Assert.Equal(ImportJobStatus.Pending, job.Status);
        Assert.Equal(await File.ReadAllBytesAsync(LegacyFilePath), input.Content);
    }

    // ---------- Oversized input ----------

    [SqlServerFact]
    public async Task Post_WithFileOverLimit_ReturnsPayloadTooLargeAndCreatesNoJob()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        var fileSize = (int)new FileInfo(LegacyFilePath).Length;

        using var factory = new ApiFactory(
            ConnectionString,
            _contentRoot.FullName,
            maxInputBytes: fileSize - 1);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(ImportEndpoint, content: null);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"maximum import size of {fileSize - 1} bytes", body);
        Assert.DoesNotContain(_contentRoot.FullName, body);

        Assert.Equal(0, await QueryAsync(context => context.ImportLogs.CountAsync()));
        Assert.Equal(0, await CountInputsAsync());
    }

    [SqlServerFact]
    public async Task Post_WithFileExactlyAtLimit_IsAccepted()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        using var factory = new ApiFactory(
            ConnectionString,
            _contentRoot.FullName,
            maxInputBytes: (int)new FileInfo(LegacyFilePath).Length);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(ImportEndpoint, content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await CountInputsAsync());
    }

    // ---------- Transaction atomicity ----------

    // The job row has already been inserted when saving its input fails.
    [SqlServerFact]
    public async Task Post_WhenSavingInputFails_LeavesNoJob()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        using var factory = new ApiFactory(
            ConnectionString,
            _contentRoot.FullName,
            [new FailCommandInterceptor("[ImportJobInputs]")]);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(ImportEndpoint, content: null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await QueryAsync(context => context.ImportLogs.CountAsync()));
        Assert.Equal(0, await CountInputsAsync());
    }

    [SqlServerFact]
    public async Task Post_WhenCommitFails_LeavesNeitherJobNorInput()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        using var factory = new ApiFactory(
            ConnectionString,
            _contentRoot.FullName,
            [new FailOnCommitInterceptor()]);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(ImportEndpoint, content: null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await QueryAsync(context => context.ImportLogs.CountAsync()));
        Assert.Equal(0, await CountInputsAsync());
    }

    // ---------- File replacement and deletion ----------

    [SqlServerFact]
    public async Task FileReplacedAfterAccepted_JobImportsWhatWasAccepted()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        var location = await AcceptJobThenStopAsync();

        await WriteLegacyFileAsync(ReplacementLine);

        var job = await RunUntilFinishedAsync(location);

        Assert.Equal("Completed", job.Status);
        Assert.Equal(1, job.Created);
        await AssertOnlyAcceptedCustomerExistsAsync();
    }

    [SqlServerFact]
    public async Task FileDeletedAfterAccepted_JobStillCompletes()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        var location = await AcceptJobThenStopAsync();

        File.Delete(LegacyFilePath);

        var job = await RunUntilFinishedAsync(location);

        Assert.Equal("Completed", job.Status);
        await AssertOnlyAcceptedCustomerExistsAsync();
    }

    // Each job keeps the input it was accepted with, even when a file change falls
    // between them.
    [SqlServerFact]
    public async Task JobsAcceptedAroundAFileChange_EachImportTheirOwnInput()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        var first = await AcceptJobThenStopAsync();

        await WriteLegacyFileAsync(ReplacementLine);

        var second = await AcceptJobThenStopAsync();

        using var factory = CreateFactory(runImportWorker: true);
        using var client = factory.CreateClient();

        await ImportJobClient.WaitForTerminalStatusAsync(client, first);
        await ImportJobClient.WaitForTerminalStatusAsync(client, second);

        var names = await QueryAsync(context =>
            context.Customers.OrderBy(x => x.LegacyCustomerId).Select(x => x.Name).ToListAsync());

        Assert.Equal(new[] { "Accepted", "Replacement" }, names);
    }

    // ---------- Restart recovery ----------

    // A crash leaves the job Processing, and the file is then replaced before restart.
    [SqlServerFact]
    public async Task CrashRecovery_AfterFileWasReplaced_RetriesWithTheOriginalInput()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        var location = await AcceptJobThenStopAsync();

        await using (var crashed = CreateDbContext())
        {
            Assert.NotNull(await new ImportRepository(crashed).ClaimNextPendingJobAsync(CancellationToken.None));
        }

        Assert.Equal(ImportJobStatus.Processing, (await GetOnlyJobAsync()).Status);

        await WriteLegacyFileAsync(ReplacementLine);

        var job = await RunUntilFinishedAsync(location);

        Assert.Equal("Completed", job.Status);
        Assert.Equal(2, (await GetOnlyJobAsync()).AttemptCount);
        await AssertOnlyAcceptedCustomerExistsAsync();
    }

    // Stops the host while SQL Server is really executing the save.
    [SqlServerFact]
    public async Task Shutdown_DuringProcessing_KeepsInputForTheRestartEvenIfFileIsDeleted()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Uri location;

        using (var factory = new ApiFactory(
            ConnectionString,
            _contentRoot.FullName,
            [new SlowQueryInterceptor(CustomerInsert), new SignalOnQueryInterceptor(saving, CustomerInsert)],
            runImportWorker: true))
        {
            using var client = factory.CreateClient();

            var response = await client.PostAsync(ImportEndpoint, content: null);

            location = response.Headers.Location!;

            await saving.Task.WaitAsync(TestTimeout);
        }

        Assert.Equal(ImportJobStatus.Pending, (await GetOnlyJobAsync()).Status);
        Assert.Equal(1, await CountInputsAsync());

        File.Delete(LegacyFilePath);

        var job = await RunUntilFinishedAsync(location);

        Assert.Equal("Completed", job.Status);
        await AssertOnlyAcceptedCustomerExistsAsync();
    }

    // ---------- Jobs without stored input ----------

    // A readable file with different content is available, and must not be used.
    [SqlServerFact]
    public async Task JobWithoutStoredInput_FailsExplicitlyAndDoesNotFallBackToTheFile()
    {
        await WriteLegacyFileAsync(ReplacementLine);

        var location = await AcceptJobThenStopAsync();

        await using (var dbContext = CreateDbContext())
        {
            await dbContext.ImportJobInputs.ExecuteDeleteAsync();
        }

        var job = await RunUntilFinishedAsync(location);

        Assert.Equal("Failed", job.Status);
        Assert.Equal(
            "The import job has no stored input, so it could not run. Submit a new import. No customers were changed.",
            job.FailureReason);
        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));
    }

    [SqlServerFact]
    public async Task JobWithoutStoredInput_DoesNotStopLaterJobs()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        var withoutInput = await AcceptJobThenStopAsync();

        await using (var dbContext = CreateDbContext())
        {
            await dbContext.ImportJobInputs.ExecuteDeleteAsync();
        }

        var withInput = await AcceptJobThenStopAsync();

        using var factory = CreateFactory(runImportWorker: true);
        using var client = factory.CreateClient();

        Assert.Equal("Failed", (await ImportJobClient.WaitForTerminalStatusAsync(client, withoutInput)).Status);
        Assert.Equal("Completed", (await ImportJobClient.WaitForTerminalStatusAsync(client, withInput)).Status);
    }

    // ---------- Snapshot cleanup ----------

    [SqlServerFact]
    public async Task Input_IsKeptWhilePendingAndDeletedOnceCompleted()
    {
        await WriteLegacyFileAsync(AcceptedLine);

        var location = await AcceptJobThenStopAsync();

        Assert.Equal(1, await CountInputsAsync());

        await RunUntilFinishedAsync(location);

        Assert.Equal(0, await CountInputsAsync());
    }

    [SqlServerFact]
    public async Task Input_IsDeletedWhenJobFailsAndNotBeforeTheFailureIsRecorded()
    {
        await WriteLegacyFileAsync(TestData.LegacyLine());

        using var factory = new ApiFactory(
            ConnectionString,
            _contentRoot.FullName,
            [new ConcurrentImportInterceptor(ConnectionString, "0000012345")],
            runImportWorker: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(ImportEndpoint, content: null);

        var job = await ImportJobClient.WaitForTerminalStatusAsync(client, response.Headers.Location!);

        Assert.Equal("Failed", job.Status);
        Assert.Equal(0, await CountInputsAsync());
    }

    // Cleanup of a finished job must not touch one that has not run yet.
    [SqlServerFact]
    public async Task FinishingOneJob_LeavesTheInputOfOtherJobs()
    {
        var finished = await AddPendingJobAsync(new DateTime(2026, 1, 1), TestData.ToInput(AcceptedLine));
        var waiting = await AddPendingJobAsync(new DateTime(2026, 1, 2), TestData.ToInput(ReplacementLine));

        await using (var dbContext = CreateDbContext())
        {
            var repository = new ImportRepository(dbContext);

            var claimed = await repository.ClaimNextPendingJobAsync(CancellationToken.None);

            Assert.Equal(finished.Id, claimed!.Id);

            await repository.MarkJobFailedAsync(claimed.Id, "Reason", CancellationToken.None);
        }

        var remaining = await QueryAsync(context => context.ImportJobInputs.SingleAsync());

        Assert.Equal(waiting.Id, remaining.ImportLogId);
        Assert.Equal(TestData.ToInput(ReplacementLine), remaining.Content);
    }

    // A Processing job left by a crash still needs its input for the retry.
    [SqlServerFact]
    public async Task FinishingOneJob_LeavesTheInputOfAnotherProcessingJob()
    {
        var finished = await AddPendingJobAsync(new DateTime(2026, 1, 1), TestData.ToInput(AcceptedLine));
        var interrupted = await AddPendingJobAsync(new DateTime(2026, 1, 2), TestData.ToInput(ReplacementLine));

        await using (var dbContext = CreateDbContext())
        {
            var repository = new ImportRepository(dbContext);

            await repository.ClaimNextPendingJobAsync(CancellationToken.None);
            await repository.ClaimNextPendingJobAsync(CancellationToken.None);

            await repository.MarkJobFailedAsync(finished.Id, "Reason", CancellationToken.None);
        }

        var remaining = await QueryAsync(context => context.ImportJobInputs.SingleAsync());

        Assert.Equal(interrupted.Id, remaining.ImportLogId);
    }

    // Failing to record the failure must not lose the input a retry would need.
    [SqlServerFact]
    public async Task MarkFailed_WhenItsTransactionFails_KeepsJobProcessingWithItsInput()
    {
        await AddPendingJobAsync(input: TestData.ToInput(AcceptedLine));

        await using (var dbContext = CreateDbContext(new FailOnCommitInterceptor()))
        {
            var repository = new ImportRepository(dbContext);

            var claimed = await repository.ClaimNextPendingJobAsync(CancellationToken.None);

            await Assert.ThrowsAsync<FailOnCommitInterceptor.CommitFailedException>(
                () => repository.MarkJobFailedAsync(claimed!.Id, "Reason", CancellationToken.None));
        }

        Assert.Equal(ImportJobStatus.Processing, (await GetOnlyJobAsync()).Status);
        Assert.Equal(1, await CountInputsAsync());
    }
}
