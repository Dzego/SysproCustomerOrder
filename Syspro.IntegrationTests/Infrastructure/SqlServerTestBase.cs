using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Syspro.Api.Data;
using Syspro.Api.Models;

namespace Syspro.IntegrationTests.Infrastructure;

// Gives every test its own freshly migrated database, which is dropped afterwards.
// Tests therefore share no data and cannot depend on execution order. Rollback
// can be verified too, which wrapping each test in a transaction would prevent.
[Collection(SqlServerCollection.Name)]
public abstract class SqlServerTestBase : IAsyncLifetime
{
    protected const int SqlUniqueIndexViolation = 2601;
    protected const int SqlConstraintViolation = 547;
    protected const int SqlStringTruncation = 2628;

    private const string DatabaseNamePrefix = "syspro_test_";

    protected SqlServerTestBase(SqlServerFixture fixture)
    {
        ConnectionString = fixture.CreateConnectionString(
            DatabaseNamePrefix + Guid.NewGuid().ToString("N"));
    }

    protected string ConnectionString { get; }

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();

        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();

        await dbContext.Database.EnsureDeletedAsync();
    }

    protected AppDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(interceptors)
            .Options;

        return new AppDbContext(options);
    }

    // Verifying through a new context proves the data came from the database
    // rather than from the change tracker of the context that wrote it.
    protected async Task<TResult> QueryAsync<TResult>(Func<AppDbContext, Task<TResult>> query)
    {
        await using var dbContext = CreateDbContext();

        return await query(dbContext);
    }

    protected async Task<Customer> AddCustomerAsync(
        string legacyCustomerId = "0000012345",
        string name = "John Smith")
    {
        await using var dbContext = CreateDbContext();

        var customer = TestData.Customer(legacyCustomerId, name);

        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        return customer;
    }
}
