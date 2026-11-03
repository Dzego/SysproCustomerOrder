using Microsoft.EntityFrameworkCore;
using Syspro.Api.Models;
using Syspro.Api.Services;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

public sealed class OrderPersistenceTests(SqlServerFixture fixture) : SqlServerTestBase(fixture)
{
    private static readonly DateTime From = new(2026, 1, 1);
    private static readonly DateTime To = new(2026, 1, 31, 23, 59, 59);

    private async Task AddOrdersAsync(params Order[] orders)
    {
        await using var dbContext = CreateDbContext();

        dbContext.Orders.AddRange(orders);
        await dbContext.SaveChangesAsync();
    }

    private async Task<List<Syspro.Api.DTOs.CustomerOrderTotalResponse>> GetTotalsAsync(
        DateTime fromDate,
        DateTime toDate)
    {
        await using var dbContext = CreateDbContext();

        return await new OrderService(dbContext)
            .GetCustomerTotalsAsync(fromDate, toDate, CancellationToken.None);
    }

    // ---------- Creating and retrieving orders ----------

    [SqlServerFact]
    public async Task CreateAsync_PersistsTheOrderAndItsItems()
    {
        var customer = await AddCustomerAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);

        await using var dbContext = CreateDbContext();

        var created = await new OrderService(dbContext).CreateAsync(
            TestData.OrderRequest(
                customerId: customer.Id,
                items:
                [
                    TestData.ItemRequest("SKU-001", "Keyboard ñ 日本", 19.99m, 3),
                    TestData.ItemRequest("SKU-002", "", 0m, 1)
                ]),
            CancellationToken.None);

        var stored = await QueryAsync(context =>
            context.Orders.Include(x => x.Items).SingleAsync());

        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal("ZAR", stored.Currency);
        Assert.Equal("Created", stored.Status);
        Assert.InRange(stored.OrderDate, before, DateTime.UtcNow.AddSeconds(1));

        Assert.Collection(
            stored.Items.OrderBy(x => x.Sku),
            item =>
            {
                Assert.Equal("Keyboard ñ 日本", item.Description);
                Assert.Equal(19.99m, item.UnitPrice);
                Assert.Equal(3, item.Quantity);
            },
            item =>
            {
                Assert.Equal(string.Empty, item.Description);
                Assert.Equal(0m, item.UnitPrice);
            });
    }

    [SqlServerFact]
    public async Task GetByIdAsync_ReturnsThatOrderWithOnlyItsOwnItemsAndTotals()
    {
        var customer = await AddCustomerAsync();

        var wanted = TestData.Order(
            customer.Id,
            items: [TestData.Item(19.99m, 3, "SKU-001"), TestData.Item(0.01m, 1, "SKU-002")]);
        var other = TestData.Order(customer.Id, items: TestData.Item(999m, 1, "SKU-003"));

        await AddOrdersAsync(wanted, other);

        await using var dbContext = CreateDbContext();

        var result = await new OrderService(dbContext).GetByIdAsync(wanted.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(59.98m, result.Total);
        Assert.Equal(
            new[] { 59.97m, 0.01m },
            result.Items.OrderBy(x => x.Sku).Select(x => x.LineTotal));
    }

    [SqlServerFact]
    public async Task GetByIdAsync_WhenOrderDoesNotExist_ReturnsNull()
    {
        await using var dbContext = CreateDbContext();

        Assert.Null(await new OrderService(dbContext).GetByIdAsync(999, CancellationToken.None));
    }

    // SQL Server compares this varchar column ignoring case and trailing spaces,
    // which InMemory does not, so only this test covers the production behaviour.
    [SqlServerTheory]
    [InlineData("ABC123")]
    [InlineData("abc123")]
    [InlineData("ABC123 ")]
    public async Task CreateAsync_FindsCustomerUsingSqlServerStringComparison(string legacyCustomerId)
    {
        var customer = await AddCustomerAsync("ABC123");

        await using var dbContext = CreateDbContext();

        var created = await new OrderService(dbContext).CreateAsync(
            TestData.OrderRequest(legacyCustomerId: legacyCustomerId),
            CancellationToken.None);

        Assert.Equal(customer.Id, created.CustomerId);
    }

    // ---------- Atomicity ----------

    [SqlServerFact]
    public async Task CreateAsync_WhenAnItemCannotBeStored_RollsBackTheWholeOrder()
    {
        var customer = await AddCustomerAsync();

        await using var dbContext = CreateDbContext();

        // Reachable only by bypassing the validator, which rejects this length.
        var action = () => new OrderService(dbContext).CreateAsync(
            TestData.OrderRequest(
                customerId: customer.Id,
                items:
                [
                    TestData.ItemRequest("SKU-001"),
                    TestData.ItemRequest(new string('S', 51))
                ]),
            CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(action);

        Assert.Equal(0, await QueryAsync(context => context.Orders.CountAsync()));
        Assert.Equal(0, await QueryAsync(context => context.OrderItems.CountAsync()));
    }

    // ---------- Customer totals ----------

    [SqlServerFact]
    public async Task GetCustomerTotals_IncludesBothRangeBoundariesAndExcludesOutsideThem()
    {
        var customer = await AddCustomerAsync();

        await AddOrdersAsync(
            TestData.Order(customer.Id, From.AddTicks(-10), TestData.Item(1000m)),
            TestData.Order(customer.Id, From, TestData.Item(1m)),
            TestData.Order(customer.Id, To, TestData.Item(2m)),
            TestData.Order(customer.Id, To.AddTicks(10), TestData.Item(2000m)));

        var total = Assert.Single(await GetTotalsAsync(From, To));

        Assert.Equal(3m, total.Total);
    }

    [SqlServerFact]
    public async Task GetCustomerTotals_SumsAcrossOrdersAndSortsHighestFirst()
    {
        var low = await AddCustomerAsync("0000000001", "Low");
        var high = await AddCustomerAsync("0000000002", "High");
        var none = await AddCustomerAsync("0000000003", "None");
        var outside = await AddCustomerAsync("0000000004", "Outside");

        await AddOrdersAsync(
            TestData.Order(low.Id, From, TestData.Item(19.99m, 3)),
            TestData.Order(high.Id, From, TestData.Item(100.10m, 2), TestData.Item(0.01m, 5)),
            TestData.Order(high.Id, To, TestData.Item(50m)),
            TestData.Order(outside.Id, To.AddDays(1), TestData.Item(9999m)));

        var totals = await GetTotalsAsync(From, To);

        Assert.Equal(new[] { "High", "Low" }, totals.Take(2).Select(x => x.Name));
        Assert.Equal(250.25m, totals[0].Total);
        Assert.Equal(high.LegacyCustomerId, totals[0].LegacyCustomerId);
        Assert.Equal(59.97m, totals[1].Total);

        // Customers without an order in the range are listed with a zero total.
        Assert.Equal(
            new[] { none.Id, outside.Id }.Order(),
            totals.Skip(2).Select(x => x.CustomerId).Order());
        Assert.All(totals.Skip(2), x => Assert.Equal(0m, x.Total));
    }

    // The largest price and quantity the validators allow must not break totals.
    [SqlServerFact]
    public async Task GetCustomerTotals_HandlesTheLargestValuesTheValidatorsAllow()
    {
        const decimal maxPrice = 9_999_999_999_999_999.99m;

        var customer = await AddCustomerAsync();

        await AddOrdersAsync(TestData.Order(customer.Id, From, TestData.Item(maxPrice, int.MaxValue)));

        var total = Assert.Single(await GetTotalsAsync(From, To));

        Assert.Equal(maxPrice * int.MaxValue, total.Total);
    }

    // ---------- Cancellation ----------

    [SqlServerFact]
    public async Task CreateAsync_WhenCancelledBeforeSaving_PersistsNothing()
    {
        var customer = await AddCustomerAsync();

        using var cancellationSource = new CancellationTokenSource();

        await using var dbContext = CreateDbContext(new CancelOnSaveInterceptor(cancellationSource));

        var action = () => new OrderService(dbContext).CreateAsync(
            TestData.OrderRequest(customerId: customer.Id),
            cancellationSource.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Equal(0, await QueryAsync(context => context.Orders.CountAsync()));
    }
}
