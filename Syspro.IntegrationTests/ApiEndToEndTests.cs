using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

// The full HTTP pipeline, dependency injection and SQL Server working together.
public sealed class ApiEndToEndTests : SqlServerTestBase, IDisposable
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ApiEndToEndTests(SqlServerFixture fixture)
        : base(fixture)
    {
        _factory = new ApiFactory(ConnectionString);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [SqlServerFact]
    public void Api_UsesTheTestDatabase()
    {
        using var scope = _factory.Services.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(ConnectionString, dbContext.Database.GetConnectionString());
    }

    // Uses the sample file committed at LegacyData/customers_legacy.dat.
    [SqlServerFact]
    public async Task Import_ThenCreateAndRetrieveOrder_ThenReportTotals()
    {
        // Import
        var importResponse = await _client.PostAsync("/api/import/customers", content: null);

        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);

        var import = await importResponse.Content.ReadFromJsonAsync<ImportResult>();

        Assert.NotNull(import);
        Assert.Equal(7, import.Processed);
        Assert.Equal(5, import.Created);
        Assert.Equal(0, import.Updated);
        Assert.Equal(2, import.Failed);

        Assert.Equal(5, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.Equal(2, await QueryAsync(context => context.ImportErrors.CountAsync()));

        // Create an order using an imported customer's legacy ID
        var createResponse = await _client.PostAsJsonAsync(
            "/api/orders",
            TestData.OrderRequest(
                legacyCustomerId: "0000012346",
                items: TestData.ItemRequest(unitPrice: 19.99m, quantity: 3)));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<OrderResponse>();

        // Retrieve it from the Location the API returned
        var retrieved = await _client.GetFromJsonAsync<OrderResponse>(createResponse.Headers.Location);

        Assert.NotNull(created);
        Assert.NotNull(retrieved);
        Assert.Equal(created.Id, retrieved.Id);
        Assert.Equal(59.97m, retrieved.Total);

        // Report totals
        var from = DateTime.UtcNow.AddMinutes(-5).ToString("O");
        var to = DateTime.UtcNow.AddMinutes(5).ToString("O");

        var totals = await _client.GetFromJsonAsync<List<CustomerOrderTotalResponse>>(
            $"/api/customers/totals?fromDate={Uri.EscapeDataString(from)}&toDate={Uri.EscapeDataString(to)}");

        Assert.NotNull(totals);
        Assert.Equal(5, totals.Count);
        Assert.Equal("0000012346", totals[0].LegacyCustomerId);
        Assert.Equal(59.97m, totals[0].Total);
    }

    [SqlServerFact]
    public async Task Import_RunTwice_UpdatesInsteadOfDuplicating()
    {
        await _client.PostAsync("/api/import/customers", content: null);

        var response = await _client.PostAsync("/api/import/customers", content: null);
        var import = await response.Content.ReadFromJsonAsync<ImportResult>();

        Assert.NotNull(import);
        Assert.Equal(0, import.Created);
        Assert.Equal(5, import.Updated);
        Assert.Equal(5, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.Equal(2, await QueryAsync(context => context.ImportLogs.CountAsync()));
    }

    [SqlServerFact]
    public async Task CreateOrder_ForUnknownCustomer_ReturnsNotFoundAndStoresNothing()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/orders",
            TestData.OrderRequest(legacyCustomerId: "9999999999"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await QueryAsync(context => context.Orders.CountAsync()));
    }

    [SqlServerFact]
    public async Task Import_WithDuplicateAndOverLongRows_ReportsThemAndSavesTheRest()
    {
        await using var legacyFile = await LegacyFileFixture.CreateAsync(
            TestData.LegacyLine(name: "First"),
            TestData.LegacyLine(name: "Second"),
            TestData.LegacyLine(legacyCustomerId: "0000000002") + "EXTRA",
            TestData.LegacyLine(legacyCustomerId: "0000000003"));

        using var factory = new ApiFactory(ConnectionString, legacyFile.ContentRoot);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/import/customers", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var import = await response.Content.ReadFromJsonAsync<ImportResult>();

        Assert.NotNull(import);
        Assert.Equal(4, import.Processed);
        Assert.Equal(2, import.Created);
        Assert.Equal(0, import.Updated);
        Assert.Equal(2, import.Failed);

        Assert.Equal(2, await QueryAsync(context => context.Customers.CountAsync()));
        Assert.Equal(2, await QueryAsync(context => context.ImportErrors.CountAsync()));
    }

    // A real unique index failure must reach the client without SQL Server details.
    [SqlServerFact]
    public async Task Import_WhenSaveFails_ReturnsGenericServerError()
    {
        await using var legacyFile = await LegacyFileFixture.CreateAsync(TestData.LegacyLine());

        using var factory = new ApiFactory(
            ConnectionString,
            legacyFile.ContentRoot,
            [new ConcurrentImportInterceptor(ConnectionString, "0000012345")]);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/import/customers", content: null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("IX_Customers", body);
        Assert.DoesNotContain("duplicate key", body);
        Assert.DoesNotContain("syspro_test_", body);
    }

    // The import endpoint reads a fixed path under the content root.
    private sealed class LegacyFileFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory;

        private LegacyFileFixture(DirectoryInfo directory)
        {
            _directory = directory;
        }

        public string ContentRoot => _directory.FullName;

        public static async Task<LegacyFileFixture> CreateAsync(params string[] lines)
        {
            var directory = Directory.CreateTempSubdirectory();

            Directory.CreateDirectory(Path.Combine(directory.FullName, "LegacyData"));

            await File.WriteAllLinesAsync(
                Path.Combine(directory.FullName, "LegacyData", "customers_legacy.dat"),
                lines);

            return new LegacyFileFixture(directory);
        }

        public ValueTask DisposeAsync()
        {
            _directory.Delete(recursive: true);

            return ValueTask.CompletedTask;
        }
    }
}
