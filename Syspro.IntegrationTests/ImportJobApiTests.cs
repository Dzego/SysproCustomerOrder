using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

// Runs the real API and hosted worker against SQL Server. Disposing a factory stops
// its host, which is how these tests simulate an application restart.
public sealed class ImportJobApiTests(SqlServerFixture fixture) : SqlServerTestBase(fixture)
{
    private const string ImportEndpoint = "/api/import/customers";

    // EF Core saves several new rows as one MERGE, and the sample file has five.
    private const string CustomerInsert = "MERGE [Customers]";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static async Task<string> CreateContentRootAsync(params string[] lines)
    {
        var directory = Directory.CreateTempSubdirectory();

        Directory.CreateDirectory(Path.Combine(directory.FullName, "LegacyData"));

        await File.WriteAllLinesAsync(
            Path.Combine(directory.FullName, "LegacyData", "customers_legacy.dat"),
            lines);

        return directory.FullName;
    }

    // ---------- Accepting a job ----------

    [SqlServerFact]
    public async Task Post_ReturnsAcceptedWithLocationAndPersistsPendingJob()
    {
        // With no worker running, the job can only be Pending if the request did
        // not wait for the import.
        using var factory = new ApiFactory(ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(ImportEndpoint, content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var accepted = await response.Content.ReadFromJsonAsync<ImportJobResponse>();

        Assert.NotNull(accepted);
        Assert.Equal("Pending", accepted.Status);
        Assert.Null(accepted.StartedAt);
        Assert.EndsWith($"/api/import/jobs/{accepted.Id}", response.Headers.Location!.ToString());

        var stored = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(accepted.Id, stored.Id);
        Assert.Equal(ImportJobStatus.Pending, stored.Status);
        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));
    }

    [SqlServerFact]
    public async Task GetLocation_ReturnsTheSameJob()
    {
        using var factory = new ApiFactory(ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(ImportEndpoint, content: null);

        var job = await client.GetFromJsonAsync<ImportJobResponse>(response.Headers.Location);

        Assert.NotNull(job);
        Assert.Equal("Pending", job.Status);
    }

    [SqlServerFact]
    public async Task Get_ForUnknownJob_ReturnsProblemDetailsNotFound()
    {
        using var factory = new ApiFactory(ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/import/jobs/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---------- Background processing ----------

    [SqlServerFact]
    public async Task Worker_CompletesAcceptedJobWithResultsAndRowErrors()
    {
        using var factory = new ApiFactory(ConnectionString, runImportWorker: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(ImportEndpoint, content: null);

        var job = await ImportJobClient.WaitForTerminalStatusAsync(client, response.Headers.Location!);

        Assert.Equal("Completed", job.Status);
        Assert.Equal(7, job.Processed);
        Assert.Equal(5, job.Created);
        Assert.Equal(0, job.Updated);
        Assert.Equal(2, job.Failed);
        Assert.Null(job.FailureReason);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);
        Assert.Equal(DateTimeKind.Utc, job.CompletedAt.Value.Kind);

        Assert.Equal(5, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.Equal(2, await QueryAsync(context => context.ImportErrors.CountAsync()));
    }

    [SqlServerFact]
    public async Task Worker_RunsQueuedJobsOnceEach_SecondUpdatesInsteadOfDuplicating()
    {
        using var factory = new ApiFactory(ConnectionString, runImportWorker: true);
        using var client = factory.CreateClient();

        var first = await client.PostAsync(ImportEndpoint, content: null);
        var second = await client.PostAsync(ImportEndpoint, content: null);

        var firstJob = await ImportJobClient.WaitForTerminalStatusAsync(client, first.Headers.Location!);
        var secondJob = await ImportJobClient.WaitForTerminalStatusAsync(client, second.Headers.Location!);

        Assert.Equal(5, firstJob.Created);
        Assert.Equal(0, secondJob.Created);
        Assert.Equal(5, secondJob.Updated);
        Assert.Equal(5, await QueryAsync(context => context.Customers.CountAsync()));

        // Each run logs its own two invalid rows.
        Assert.Equal(4, await QueryAsync(context => context.ImportErrors.CountAsync()));

        var attempts = await QueryAsync(context =>
            context.ImportLogs.Select(x => x.AttemptCount).ToListAsync());

        Assert.All(attempts, attempt => Assert.Equal(1, attempt));
    }

    [SqlServerFact]
    public async Task Worker_ProcessesDuplicateAndOverLongRowsUsingExistingRules()
    {
        var contentRoot = await CreateContentRootAsync(
            TestData.LegacyLine(name: "First"),
            TestData.LegacyLine(name: "Second"),
            TestData.LegacyLine(legacyCustomerId: "0000000002") + "EXTRA",
            TestData.LegacyLine(legacyCustomerId: "0000000003"));

        try
        {
            using var factory = new ApiFactory(ConnectionString, contentRoot, runImportWorker: true);
            using var client = factory.CreateClient();

            var response = await client.PostAsync(ImportEndpoint, content: null);

            var job = await ImportJobClient.WaitForTerminalStatusAsync(client, response.Headers.Location!);

            Assert.Equal("Completed", job.Status);
            Assert.Equal(4, job.Processed);
            Assert.Equal(2, job.Created);
            Assert.Equal(2, job.Failed);

            var first = await QueryAsync(context =>
                context.Customers.SingleAsync(x => x.LegacyCustomerId == "0000012345"));

            Assert.Equal("First", first.Name);
            Assert.Equal(2, await QueryAsync(context => context.ImportErrors.CountAsync()));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    // A real unique index failure must not reach the client through the job status.
    [SqlServerFact]
    public async Task Worker_WhenSaveFails_ReportsSafeFailureAndKeepsRunning()
    {
        var contentRoot = await CreateContentRootAsync(TestData.LegacyLine());

        try
        {
            using var factory = new ApiFactory(
                ConnectionString,
                contentRoot,
                [new ConcurrentImportInterceptor(ConnectionString, "0000012345")],
                runImportWorker: true);
            using var client = factory.CreateClient();

            var response = await client.PostAsync(ImportEndpoint, content: null);

            var location = response.Headers.Location!;

            var failed = await ImportJobClient.WaitForTerminalStatusAsync(client, location);

            Assert.Equal("Failed", failed.Status);
            Assert.Equal(0, failed.Created);

            var body = await client.GetStringAsync(location);

            Assert.Equal(
                "The import could not be saved to the database. No customers were changed.",
                JsonDocument.Parse(body).RootElement.GetProperty("failureReason").GetString());
            Assert.DoesNotContain("IX_Customers", body);
            Assert.DoesNotContain("duplicate key", body);
            Assert.DoesNotContain("syspro_test_", body);

            // The worker survives, and the interceptor only interferes once.
            var next = await client.PostAsync(ImportEndpoint, content: null);

            var completed = await ImportJobClient.WaitForTerminalStatusAsync(client, next.Headers.Location!);

            Assert.Equal("Completed", completed.Status);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    // ---------- Client cancellation ----------

    // The client leaves before the job is saved, so there must be no job at all.
    [SqlServerFact]
    public async Task ClientCancellation_BeforeJobIsPersisted_LeavesNoJob()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var factory = new ApiFactory(
            ConnectionString,
            interceptors:
            [
                new SlowQueryInterceptor("INSERT INTO [ImportLogs]"),
                new SignalOnQueryInterceptor(saving, "INSERT INTO [ImportLogs]")
            ]);
        using var client = factory.CreateClient();
        using var cancellationSource = new CancellationTokenSource();

        var sendTask = client.PostAsync(ImportEndpoint, content: null, cancellationSource.Token);

        await saving.Task.WaitAsync(TestTimeout);

        cancellationSource.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        var statusCode = await factory.Outcome.StatusCode.Task.WaitAsync(TestTimeout);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, statusCode);
        Assert.Equal(0, await QueryAsync(context => context.ImportLogs.CountAsync()));
    }

    // Accept the job, then lose the client completely before any work has begun.
    [SqlServerFact]
    public async Task ClientDisconnect_AfterAccepted_DoesNotAffectTheJob()
    {
        int jobId;

        using (var accepting = new ApiFactory(ConnectionString))
        using (var client = accepting.CreateClient())
        {
            using var cancellationSource = new CancellationTokenSource();

            var response = await client.PostAsync(ImportEndpoint, content: null, cancellationSource.Token);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            jobId = (await response.Content.ReadFromJsonAsync<ImportJobResponse>())!.Id;

            cancellationSource.Cancel();
        }

        using var factory = new ApiFactory(ConnectionString, runImportWorker: true);
        using var statusClient = factory.CreateClient();

        var job = await ImportJobClient.WaitForTerminalStatusAsync(
            statusClient,
            new Uri($"/api/import/jobs/{jobId}", UriKind.Relative));

        Assert.Equal("Completed", job.Status);
    }

    // ---------- Restart and shutdown ----------

    [SqlServerFact]
    public async Task Restart_RunsJobThatWasPendingWhenTheApiStopped()
    {
        Uri location;

        using (var beforeRestart = new ApiFactory(ConnectionString))
        using (var client = beforeRestart.CreateClient())
        {
            var response = await client.PostAsync(ImportEndpoint, content: null);

            location = response.Headers.Location!;
        }

        Assert.Equal(ImportJobStatus.Pending, (await QueryAsync(context => context.ImportLogs.SingleAsync())).Status);

        using var afterRestart = new ApiFactory(ConnectionString, runImportWorker: true);
        using var restartedClient = afterRestart.CreateClient();

        var job = await ImportJobClient.WaitForTerminalStatusAsync(restartedClient, location);

        Assert.Equal("Completed", job.Status);
        Assert.Equal(5, job.Created);
    }

    // A crash cannot run any shutdown code, so its row is left Processing.
    [SqlServerFact]
    public async Task Restart_RecoversJobLeftProcessingByACrash()
    {
        int jobId;

        await using (var dbContext = CreateDbContext())
        {
            var now = DateTime.UtcNow;

            var job = new ImportLog
            {
                Status = ImportJobStatus.Processing,
                CreatedAt = now,
                StartedAt = now,
                AttemptCount = 1,
                Input = new ImportJobInput { Content = TestData.ToInput(TestData.LegacyLine()) }
            };

            dbContext.ImportLogs.Add(job);
            await dbContext.SaveChangesAsync();

            jobId = job.Id;
        }

        using var factory = new ApiFactory(ConnectionString, runImportWorker: true);
        using var client = factory.CreateClient();

        var result = await ImportJobClient.WaitForTerminalStatusAsync(
            client,
            new Uri($"/api/import/jobs/{jobId}", UriKind.Relative));

        Assert.Equal("Completed", result.Status);
        Assert.Equal(1, result.Created);

        var stored = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(2, stored.AttemptCount);
    }

    // Stops the host while SQL Server is really executing the save.
    [SqlServerFact]
    public async Task Shutdown_DuringProcessing_DoesNotCompleteTheJob_AndRestartFinishesIt()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Uri location;

        using (var factory = new ApiFactory(
            ConnectionString,
            interceptors:
            [
                new SlowQueryInterceptor(CustomerInsert),
                new SignalOnQueryInterceptor(saving, CustomerInsert)
            ],
            runImportWorker: true))
        {
            using var client = factory.CreateClient();

            var response = await client.PostAsync(ImportEndpoint, content: null);

            location = response.Headers.Location!;

            await saving.Task.WaitAsync(TestTimeout);

            var processing = await client.GetFromJsonAsync<ImportJobResponse>(location);

            Assert.Equal("Processing", processing!.Status);
        }

        var interrupted = await QueryAsync(context => context.ImportLogs.SingleAsync());

        Assert.Equal(ImportJobStatus.Pending, interrupted.Status);
        Assert.Null(interrupted.CompletedAt);
        Assert.Equal(1, interrupted.AttemptCount);
        Assert.Equal(0, interrupted.ConsecutiveFailureCount);
        Assert.Equal(0, await QueryAsync(context => context.Customers.CountAsync()));

        using var restarted = new ApiFactory(ConnectionString, runImportWorker: true);
        using var restartedClient = restarted.CreateClient();

        var job = await ImportJobClient.WaitForTerminalStatusAsync(restartedClient, location);

        Assert.Equal("Completed", job.Status);
        Assert.Equal(5, job.Created);
    }
}
