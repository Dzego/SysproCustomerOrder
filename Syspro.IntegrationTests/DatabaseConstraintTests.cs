using Microsoft.EntityFrameworkCore;
using Syspro.Api.Models;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

// None of these rules exist under EF Core InMemory, which enforces no unique
// indexes, column lengths, decimal precision or database-level delete rules.
public sealed class DatabaseConstraintTests(SqlServerFixture fixture) : SqlServerTestBase(fixture)
{
    public static TheoryData<decimal> StorableUnitPrices => new()
    {
        0m,
        0.01m,
        10.99m,
        9_999_999_999_999_999.99m
    };

    // ---------- Unique legacy customer IDs ----------

    // The default collation ignores case and SQL Server ignores trailing spaces
    // when comparing, so these all count as the same legacy ID.
    [SqlServerTheory]
    [InlineData("ABC123", "ABC123")]
    [InlineData("ABC123", "abc123")]
    [InlineData("ABC123", "ABC123  ")]
    public async Task Customer_WithDuplicateLegacyId_IsRejected(string existing, string duplicate)
    {
        await AddCustomerAsync(existing);

        await using var dbContext = CreateDbContext();
        dbContext.Customers.Add(TestData.Customer(duplicate));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        Assert.Equal(SqlUniqueIndexViolation, TestData.SqlErrorNumber(exception));
        Assert.Equal(1, await QueryAsync(context => context.Customers.CountAsync()));
    }

    [SqlServerFact]
    public async Task Customers_WithDifferentLegacyIds_AreBothStored()
    {
        await AddCustomerAsync("0000000001");
        await AddCustomerAsync("0000000002");

        Assert.Equal(2, await QueryAsync(context => context.Customers.CountAsync()));
    }

    // ---------- Foreign keys and delete rules ----------

    [SqlServerFact]
    public async Task Order_ForUnknownCustomer_IsRejected()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Orders.Add(TestData.Order(customerId: 999, items: TestData.Item()));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        Assert.Equal(SqlConstraintViolation, TestData.SqlErrorNumber(exception));
    }

    [SqlServerFact]
    public async Task OrderItem_ForUnknownOrder_IsRejected()
    {
        await using var dbContext = CreateDbContext();

        var item = TestData.Item();
        item.OrderId = 999;
        dbContext.OrderItems.Add(item);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        Assert.Equal(SqlConstraintViolation, TestData.SqlErrorNumber(exception));
    }

    [SqlServerFact]
    public async Task ImportError_ForUnknownImportLog_IsRejected()
    {
        await using var dbContext = CreateDbContext();
        dbContext.ImportErrors.Add(new ImportError
        {
            ImportLogId = 999,
            LineNumber = 1,
            RawData = "raw",
            Reason = "reason"
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        Assert.Equal(SqlConstraintViolation, TestData.SqlErrorNumber(exception));
    }

    [SqlServerFact]
    public async Task DeletingCustomerWithOrders_IsRejectedAndKeepsTheOrders()
    {
        var customer = await AddCustomerAsync();

        await using (var dbContext = CreateDbContext())
        {
            dbContext.Orders.Add(TestData.Order(customer.Id, items: TestData.Item()));
            await dbContext.SaveChangesAsync();
        }

        await using var deleteContext = CreateDbContext();

        var exception = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => deleteContext.Customers.ExecuteDeleteAsync());

        Assert.Equal(SqlConstraintViolation, exception.Number);
        Assert.Equal(1, await QueryAsync(context => context.Orders.CountAsync()));
    }

    [SqlServerFact]
    public async Task DeletingOrder_CascadesToItsItemsOnly()
    {
        var customer = await AddCustomerAsync();

        var deleted = TestData.Order(customer.Id, items: [TestData.Item(), TestData.Item(sku: "SKU-002")]);
        var kept = TestData.Order(customer.Id, items: TestData.Item(sku: "SKU-003"));

        await using (var dbContext = CreateDbContext())
        {
            dbContext.Orders.AddRange(deleted, kept);
            await dbContext.SaveChangesAsync();
        }

        await using (var dbContext = CreateDbContext())
        {
            await dbContext.Orders.Where(x => x.Id == deleted.Id).ExecuteDeleteAsync();
        }

        var remaining = await QueryAsync(context => context.OrderItems.ToListAsync());

        Assert.Equal("SKU-003", Assert.Single(remaining).Sku);
    }

    [SqlServerFact]
    public async Task DeletingImportLog_CascadesToItsErrors()
    {
        await using (var dbContext = CreateDbContext())
        {
            var log = new ImportLog { StartedAt = DateTime.UtcNow };
            log.Errors.Add(new ImportError { LineNumber = 1, RawData = "raw", Reason = "reason" });

            dbContext.ImportLogs.Add(log);
            await dbContext.SaveChangesAsync();
        }

        await using (var dbContext = CreateDbContext())
        {
            await dbContext.ImportLogs.ExecuteDeleteAsync();
        }

        Assert.Equal(0, await QueryAsync(context => context.ImportErrors.CountAsync()));
    }

    // ---------- Decimal precision and scale ----------

    [SqlServerTheory]
    [MemberData(nameof(StorableUnitPrices))]
    public async Task UnitPrice_WithinPrecisionAndScale_RoundTripsExactly(decimal unitPrice)
    {
        var customer = await AddCustomerAsync();

        await using (var dbContext = CreateDbContext())
        {
            dbContext.Orders.Add(TestData.Order(customer.Id, items: TestData.Item(unitPrice)));
            await dbContext.SaveChangesAsync();
        }

        var stored = await QueryAsync(context => context.OrderItems.SingleAsync());

        Assert.Equal(unitPrice, stored.UnitPrice);
    }

    [SqlServerFact]
    public async Task UnitPrice_OverPrecision_IsRejected()
    {
        var customer = await AddCustomerAsync();

        await using var dbContext = CreateDbContext();
        dbContext.Orders.Add(TestData.Order(
            customer.Id,
            items: TestData.Item(10_000_000_000_000_000m)));

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.Equal(0, await QueryAsync(context => context.OrderItems.CountAsync()));
    }

    // The validator rejects this price because the column cannot hold it exactly.
    [SqlServerFact]
    public async Task UnitPrice_OverScale_IsNotStoredExactly()
    {
        var customer = await AddCustomerAsync();

        await using (var dbContext = CreateDbContext())
        {
            dbContext.Orders.Add(TestData.Order(customer.Id, items: TestData.Item(10.999m)));
            await dbContext.SaveChangesAsync();
        }

        var stored = await QueryAsync(context => context.OrderItems.SingleAsync());

        Assert.NotEqual(10.999m, stored.UnitPrice);
    }

    // ---------- Column lengths and Unicode ----------

    [SqlServerFact]
    public async Task Sku_OverColumnLength_IsRejectedRatherThanTruncated()
    {
        var customer = await AddCustomerAsync();

        await using var dbContext = CreateDbContext();
        dbContext.Orders.Add(TestData.Order(
            customer.Id,
            items: TestData.Item(sku: new string('S', 51))));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        Assert.Equal(SqlStringTruncation, TestData.SqlErrorNumber(exception));
    }

    [SqlServerFact]
    public async Task CustomerName_PreservesUnicodeCharacters()
    {
        const string name = "Ñandú Škoda 日本語";

        await AddCustomerAsync(name: name);

        var stored = await QueryAsync(context => context.Customers.SingleAsync());

        Assert.Equal(name, stored.Name);
    }
}
