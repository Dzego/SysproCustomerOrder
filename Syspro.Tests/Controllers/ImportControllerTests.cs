using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Xunit;

namespace Syspro.Tests.Controllers;

// The worker is disabled, so these cover only the HTTP contract.
public sealed class ImportControllerTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public ImportControllerTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                services.Configure<CustomerImportWorkerOptions>(options => options.Enabled = false);

                services.RemoveAll<DbContextOptions<AppDbContext>>();

                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(_databaseName));
            });
        }
    }

    private async Task<ImportLog> AddJobAsync(ImportLog job)
    {
        using var scope = _factory.Services.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.ImportLogs.Add(job);
        await dbContext.SaveChangesAsync();

        return job;
    }

    [Fact]
    public async Task Post_ReturnsAcceptedWithPendingJobAndLocation()
    {
        var before = DateTime.UtcNow;

        var response = await _client.PostAsync("/api/import/customers", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var job = await response.Content.ReadFromJsonAsync<ImportJobResponse>();

        Assert.NotNull(job);
        Assert.Equal("Pending", job.Status);
        Assert.InRange(job.CreatedAt, before, DateTime.UtcNow);
        Assert.Null(job.StartedAt);
        Assert.Null(job.CompletedAt);
        Assert.Equal(0, job.Processed);
        Assert.EndsWith($"/api/import/jobs/{job.Id}", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Get_ReturnsCountsTimesAndReasonForFinishedJob()
    {
        var completedAt = new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Unspecified);

        var job = await AddJobAsync(new ImportLog
        {
            Status = ImportJobStatus.Failed,
            CreatedAt = new DateTime(2026, 1, 1, 10, 0, 0),
            StartedAt = new DateTime(2026, 1, 1, 10, 0, 30),
            CompletedAt = completedAt,
            FailureReason = "Safe reason.",
            ProcessedCount = 4,
            CreatedCount = 1,
            UpdatedCount = 2,
            FailedCount = 1
        });

        var result = await _client.GetFromJsonAsync<ImportJobResponse>($"/api/import/jobs/{job.Id}");

        Assert.NotNull(result);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("Safe reason.", result.FailureReason);
        Assert.Equal(4, result.Processed);
        Assert.Equal(1, result.Created);
        Assert.Equal(2, result.Updated);
        Assert.Equal(1, result.Failed);
        Assert.Equal(completedAt, result.CompletedAt);
        Assert.Equal(DateTimeKind.Utc, result.CompletedAt!.Value.Kind);
    }

    [Fact]
    public async Task Get_DoesNotExposeInternalJobFields()
    {
        var job = await AddJobAsync(new ImportLog
        {
            Status = ImportJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            AttemptCount = 2
        });

        var body = await _client.GetStringAsync($"/api/import/jobs/{job.Id}");

        Assert.DoesNotContain("attemptCount", body);
        Assert.DoesNotContain("input", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_ForUnknownJob_ReturnsProblemDetailsNotFound()
    {
        var response = await _client.GetAsync("/api/import/jobs/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Import job with id 999 was not found.", await response.Content.ReadAsStringAsync());
    }
}
