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
        var result = await service.CreateAsync(request);

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
        var result = await service.CreateAsync(request);

        // Assert
        Assert.Equal(customer.Id, result.CustomerId);
        Assert.Equal("ZAR", result.Currency);
        Assert.Equal(1450m, result.Total);
    }

    [Fact]
    public async Task CreateAsync_WhenCustomerDoesNotExist_ThrowsKeyNotFoundException()
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
        var action = () => service.CreateAsync(request);

        // Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(action);
    }

    [Fact]
    public async Task CreateAsync_WhenItemsAreEmpty_ThrowsArgumentException()
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
        var action = () => service.CreateAsync(request);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateAsync_WhenQuantityIsNotPositive_ThrowsArgumentException(
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
        var action = () => service.CreateAsync(request);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(action);
    }
}