using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Services;
using Syspro.Tests.TestSupport;
using Xunit;

namespace Syspro.Tests.Services;

public class OrderServiceCancellationTests
{
    private readonly SaveChangesTokenCaptureInterceptor _interceptor = new();

    private AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(_interceptor)
            .Options;

        return new AppDbContext(options);
    }

    private static async Task<Customer> AddCustomerAsync(AppDbContext dbContext)
    {
        var customer = new Customer
        {
            LegacyCustomerId = "0000012345",
            Name = "John Smith",
            Email = "john.smith@example.com",
            Tier = "A",
            SignupDate = new DateOnly(2021, 3, 15)
        };

        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        return customer;
    }

    private static CreateOrderRequest CreateRequest(
        int? customerId = null,
        string? legacyCustomerId = null)
    {
        return new CreateOrderRequest
        {
            CustomerId = customerId,
            LegacyCustomerId = legacyCustomerId,
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
    }

    private static CancellationToken CreateCancelledToken()
    {
        return new CancellationToken(canceled: true);
    }

    // The customer does not exist, so a lookup that ignored the token would
    // complete and throw ResourceNotFoundException instead of cancelling.
    [Theory]
    [InlineData(999, null)]
    [InlineData(null, "9999999999")]
    public async Task CreateAsync_WhenCancelled_CancelsCustomerLookup(
        int? customerId,
        string? legacyCustomerId)
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var service = new OrderService(dbContext);

        // Act
        var action = () => service.CreateAsync(
            CreateRequest(customerId, legacyCustomerId),
            CreateCancelledToken());

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
    }

    [Fact]
    public async Task CreateAsync_PassesTokenToSaveChanges()
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var customer = await AddCustomerAsync(dbContext);
        var service = new OrderService(dbContext);

        using var cancellationSource = new CancellationTokenSource();

        // Act
        await service.CreateAsync(
            CreateRequest(customerId: customer.Id),
            cancellationSource.Token);

        // Assert
        Assert.Equal(cancellationSource.Token, _interceptor.CapturedToken);
    }

    // Cancels when saving starts, so the customer lookup has already succeeded. A
    // token that is cancelled up front would only ever exercise the lookup.
    [Fact]
    public async Task CreateAsync_WhenCancelledAtSave_DoesNotPersistOrder()
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var customer = await AddCustomerAsync(dbContext);
        var service = new OrderService(dbContext);

        using var cancellationSource = new CancellationTokenSource();
        _interceptor.OnSaving = cancellationSource.Cancel;

        // Act
        var action = () => service.CreateAsync(
            CreateRequest(customerId: customer.Id),
            cancellationSource.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Equal(0, await dbContext.Orders.CountAsync());
    }

    [Fact]
    public async Task GetByIdAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var service = new OrderService(dbContext);

        // Act
        var action = () => service.GetByIdAsync(1, CreateCancelledToken());

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
    }

    [Fact]
    public async Task GetCustomerTotalsAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var service = new OrderService(dbContext);

        // Act
        var action = () => service.GetCustomerTotalsAsync(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            CreateCancelledToken());

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
    }
}
