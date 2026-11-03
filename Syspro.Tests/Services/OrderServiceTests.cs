using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Services;
using Xunit;

namespace Syspro.Tests.Services;

public class OrderServiceTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static async Task<Customer> AddCustomerAsync(
        AppDbContext dbContext,
        string legacyCustomerId = "0000012345",
        string name = "John Smith",
        string email = "john.smith@example.com",
        string tier = "A",
        DateOnly? signupDate = null)
    {
        var customer = new Customer
        {
            LegacyCustomerId = legacyCustomerId,
            Name = name,
            Email = email,
            Tier = tier,
            SignupDate = signupDate ?? new DateOnly(2021, 3, 15)
        };

        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        return customer;
    }

    [Fact]
    public async Task CreateAsync_WithMultipleItems_ComputesCorrectOrderTotal()
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var customer = await AddCustomerAsync(dbContext);

        var service = new OrderService(dbContext);

        var request = new CreateOrderRequest
        {
            CustomerId = customer.Id,
            Currency = "ZAR",
            Status = "Created",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "SKU-001",
                    Description = "Keyboard",
                    UnitPrice = 500m,
                    Quantity = 2
                },
                new CreateOrderItemRequest
                {
                    Sku = "SKU-002",
                    Description = "Mouse",
                    UnitPrice = 250m,
                    Quantity = 1
                }
            ]
        };

        // Act
        var result = await service.CreateAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1250m, result.Total);
        Assert.Equal(2, result.Items.Count);

        Assert.Collection(
            result.Items,
            item => Assert.Equal(1000m, item.LineTotal),
            item => Assert.Equal(250m, item.LineTotal));
    }

    [Fact]
    public async Task CreateAsync_WithLegacyCustomerId_CreatesOrderForCorrectCustomer()
    {
        // Arrange
        await using var dbContext = CreateDbContext();

        var customer = await AddCustomerAsync(
            dbContext,
            legacyCustomerId: "0000012349",
            name: "Tech Solutions",
            email: "info@techsolutions.example",
            tier: "B",
            signupDate: new DateOnly(2023, 1, 20));

        var service = new OrderService(dbContext);

        var request = new CreateOrderRequest
        {
            LegacyCustomerId = "0000012349",
            Currency = "zar",
            Status = "Created",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "SKU-020",
                    Description = "External Hard Drive",
                    UnitPrice = 1450m,
                    Quantity = 1
                }
            ]
        };

        // Act
        var result = await service.CreateAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(customer.Id, result.CustomerId);
        Assert.Equal("ZAR", result.Currency);
        Assert.Equal(1450m, result.Total);
    }

    [Fact]
    public async Task CreateAsync_WhenCustomerDoesNotExist_ThrowsResourceNotFoundException()
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var service = new OrderService(dbContext);

        var request = new CreateOrderRequest
        {
            LegacyCustomerId = "9999999999",
            Currency = "ZAR",
            Status = "Created",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "SKU-001",
                    Description = "Keyboard",
                    UnitPrice = 500m,
                    Quantity = 1
                }
            ]
        };

        // Act
        var action = () => service.CreateAsync(request, CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<ResourceNotFoundException>(action);
    }

    [Fact]
    public async Task CreateAsync_WhenItemsAreEmpty_ThrowsBusinessRuleException()
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var customer = await AddCustomerAsync(dbContext);

        var service = new OrderService(dbContext);

        var request = new CreateOrderRequest
        {
            CustomerId = customer.Id,
            Currency = "ZAR",
            Status = "Created",
            Items = []
        };

        // Act
        var action = () => service.CreateAsync(request, CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<BusinessRuleException>(action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateAsync_WhenQuantityIsNotPositive_ThrowsBusinessRuleException(
        int quantity)
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var customer = await AddCustomerAsync(dbContext);

        var service = new OrderService(dbContext);

        var request = new CreateOrderRequest
        {
            CustomerId = customer.Id,
            Currency = "ZAR",
            Status = "Created",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "SKU-001",
                    Description = "Keyboard",
                    UnitPrice = 500m,
                    Quantity = quantity
                }
            ]
        };

        // Act
        var action = () => service.CreateAsync(request, CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<BusinessRuleException>(action);
    }

    private static CreateOrderRequest CreateRequest(
        int? customerId = null,
        string? legacyCustomerId = null,
        string currency = "ZAR",
        List<CreateOrderItemRequest>? items = null)
    {
        return new CreateOrderRequest
        {
            CustomerId = customerId,
            LegacyCustomerId = legacyCustomerId,
            Currency = currency,
            Status = "Created",
            Items = items ?? [CreateItem()]
        };
    }

    private static CreateOrderItemRequest CreateItem(
        string sku = "SKU-001",
        decimal unitPrice = 500m,
        int quantity = 1)
    {
        return new CreateOrderItemRequest
        {
            Sku = sku,
            Description = "Keyboard",
            UnitPrice = unitPrice,
            Quantity = quantity
        };
    }

    private static async Task AddOrderAsync(
        AppDbContext dbContext,
        int customerId,
        DateTime orderDate,
        decimal unitPrice)
    {
        dbContext.Orders.Add(new Order
        {
            CustomerId = customerId,
            OrderDate = orderDate,
            Currency = "ZAR",
            Status = "Created",
            Items =
            [
                new OrderItem
                {
                    Sku = "SKU-001",
                    Description = "Keyboard",
                    UnitPrice = unitPrice,
                    Quantity = 1
                }
            ]
        });

        await dbContext.SaveChangesAsync();
    }

    // ---------- Customer selection ----------

    [Fact]
    public async Task CreateAsync_WithBothIdentifiers_UsesCustomerId()
    {
        await using var dbContext = CreateDbContext();
        var byId = await AddCustomerAsync(dbContext, legacyCustomerId: "0000000001");
        await AddCustomerAsync(dbContext, legacyCustomerId: "0000000002");

        var result = await new OrderService(dbContext).CreateAsync(
            CreateRequest(customerId: byId.Id, legacyCustomerId: "0000000002"),
            CancellationToken.None);

        Assert.Equal(byId.Id, result.CustomerId);
    }

    [Fact]
    public async Task CreateAsync_WithUnknownCustomerIdAndValidLegacyId_DoesNotFallBack()
    {
        await using var dbContext = CreateDbContext();
        await AddCustomerAsync(dbContext, legacyCustomerId: "0000000001");

        var action = () => new OrderService(dbContext).CreateAsync(
            CreateRequest(customerId: 999, legacyCustomerId: "0000000001"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(action);

        Assert.Equal("Customer was not found.", exception.Message);
    }

    // ---------- Business rules ----------

    public static TheoryData<CreateOrderRequest, string> RequestsBreakingABusinessRule => new()
    {
        { CreateRequest(customerId: 1, items: []), "An order must contain at least one item." },
        { new CreateOrderRequest { CustomerId = 1, Currency = "ZAR", Status = "Created", Items = null! }, "An order must contain at least one item." },
        { CreateRequest(customerId: 1, currency: ""), "Currency must be a 3-character code." },
        { CreateRequest(customerId: 1, currency: "ZA"), "Currency must be a 3-character code." },
        { CreateRequest(customerId: 1, currency: "ZARR"), "Currency must be a 3-character code." },
        { CreateRequest(), "CustomerId or LegacyCustomerId must be provided." },
        { CreateRequest(legacyCustomerId: "   "), "CustomerId or LegacyCustomerId must be provided." },
        { CreateRequest(customerId: 1, items: [CreateItem(), CreateItem(quantity: 0)]), "Item quantity must be greater than zero." },
        { CreateRequest(customerId: 1, items: [CreateItem(unitPrice: -0.01m)]), "Item unit price cannot be negative." },
        { CreateRequest(customerId: 1, items: [CreateItem(sku: "   ")]), "Item SKU is required." }
    };

    [Theory]
    [MemberData(nameof(RequestsBreakingABusinessRule))]
    public async Task CreateAsync_WhenABusinessRuleIsBroken_ThrowsWithMessageAndStoresNothing(
        CreateOrderRequest request,
        string expectedMessage)
    {
        await using var dbContext = CreateDbContext();
        await AddCustomerAsync(dbContext);

        var action = () => new OrderService(dbContext).CreateAsync(request, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(action);

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Equal(0, await dbContext.Orders.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_WithZeroUnitPrice_IsAllowed()
    {
        await using var dbContext = CreateDbContext();
        var customer = await AddCustomerAsync(dbContext);

        var result = await new OrderService(dbContext).CreateAsync(
            CreateRequest(customerId: customer.Id, items: [CreateItem(unitPrice: 0m, quantity: 5)]),
            CancellationToken.None);

        Assert.Equal(0m, result.Total);
    }

    // ---------- Retrieval ----------

    [Fact]
    public async Task GetByIdAsync_ReturnsTheStoredOrderWithComputedTotals()
    {
        await using var dbContext = CreateDbContext();
        var customer = await AddCustomerAsync(dbContext);
        var service = new OrderService(dbContext);

        var created = await service.CreateAsync(
            CreateRequest(
                customerId: customer.Id,
                currency: "usd",
                items: [CreateItem(unitPrice: 19.99m, quantity: 3)]),
            CancellationToken.None);

        var result = await service.GetByIdAsync(created.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(customer.Id, result.CustomerId);
        Assert.Equal("USD", result.Currency);
        Assert.Equal("Created", result.Status);
        Assert.Equal(59.97m, result.Total);

        var item = Assert.Single(result.Items);

        Assert.Equal("SKU-001", item.Sku);
        Assert.Equal("Keyboard", item.Description);
        Assert.Equal(19.99m, item.UnitPrice);
        Assert.Equal(3, item.Quantity);
        Assert.Equal(59.97m, item.LineTotal);
    }

    [Fact]
    public async Task GetByIdAsync_WhenOrderDoesNotExist_ReturnsNull()
    {
        await using var dbContext = CreateDbContext();

        Assert.Null(await new OrderService(dbContext).GetByIdAsync(999, CancellationToken.None));
    }

    // ---------- Customer totals ----------
    // SQL translation of this query is verified in Syspro.IntegrationTests.

    [Fact]
    public async Task GetCustomerTotalsAsync_WithInvertedRange_ThrowsAndNamesTheRule()
    {
        await using var dbContext = CreateDbContext();

        var action = () => new OrderService(dbContext).GetCustomerTotalsAsync(
            new DateTime(2026, 1, 2),
            new DateTime(2026, 1, 1),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(action);

        Assert.Equal("fromDate cannot be later than toDate.", exception.Message);
    }

    [Fact]
    public async Task GetCustomerTotalsAsync_WithEqualDates_IncludesAnOrderAtThatInstant()
    {
        var instant = new DateTime(2026, 1, 1, 12, 0, 0);

        await using var dbContext = CreateDbContext();
        var customer = await AddCustomerAsync(dbContext);
        await AddOrderAsync(dbContext, customer.Id, instant, 10m);

        var totals = await new OrderService(dbContext)
            .GetCustomerTotalsAsync(instant, instant, CancellationToken.None);

        Assert.Equal(10m, Assert.Single(totals).Total);
    }

    [Fact]
    public async Task GetCustomerTotalsAsync_ListsCustomersWithoutOrdersAtZeroAfterOthers()
    {
        var date = new DateTime(2026, 1, 1);

        await using var dbContext = CreateDbContext();
        var none = await AddCustomerAsync(dbContext, legacyCustomerId: "0000000001");
        var some = await AddCustomerAsync(dbContext, legacyCustomerId: "0000000002");
        await AddOrderAsync(dbContext, some.Id, date, 10m);

        var totals = await new OrderService(dbContext)
            .GetCustomerTotalsAsync(date, date, CancellationToken.None);

        Assert.Equal(new[] { some.Id, none.Id }, totals.Select(x => x.CustomerId));
        Assert.Equal(new[] { 10m, 0m }, totals.Select(x => x.Total));
    }
}
