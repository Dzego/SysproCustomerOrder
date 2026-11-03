using System.Net;
using System.Net.Http.Json;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Validators;
using Xunit;

namespace Syspro.Tests.Controllers;

// xUnit creates a new instance of this class for every test, so each test gets
// its own host and its own in-memory database. No state is shared between tests.
public sealed class OrdersControllerTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public OrdersControllerTests()
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
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();

                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(_databaseName));
            });
        }
    }

    private async Task<Customer> AddCustomerAsync(string legacyCustomerId = "0000012345")
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = new Customer
        {
            LegacyCustomerId = legacyCustomerId,
            Name = "John Smith",
            Email = "john.smith@example.com",
            Tier = "A",
            SignupDate = new DateOnly(2021, 3, 15)
        };

        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        return customer;
    }

    private async Task<int> CountOrdersAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Orders.CountAsync();
    }

    private static object CreateValidItem(
        string sku = "SKU-001",
        string description = "Keyboard",
        decimal unitPrice = 500m,
        int quantity = 2)
    {
        return new { sku, description, unitPrice, quantity };
    }

    private async Task<ValidationProblemDetails> ReadValidationProblemAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();

        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);

        return problem;
    }

    // ---------- Dependency injection ----------

    [Fact]
    public void Validators_AreRegisteredInDependencyInjection()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.IsType<CreateOrderRequestValidator>(
            scope.ServiceProvider.GetRequiredService<IValidator<CreateOrderRequest>>());
        Assert.IsType<CreateOrderItemRequestValidator>(
            scope.ServiceProvider.GetRequiredService<IValidator<CreateOrderItemRequest>>());
    }

    // ---------- Successful creation ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateOrder_WithValidRequest_ReturnsCreatedAndOrderIsRetrievable(
        bool identifyByLegacyId)
    {
        // Arrange
        var customer = await AddCustomerAsync();

        var request = new
        {
            customerId = identifyByLegacyId ? (int?)null : customer.Id,
            legacyCustomerId = identifyByLegacyId ? customer.LegacyCustomerId : null,
            currency = "zar",
            status = "Created",
            items = new[]
            {
                CreateValidItem(sku: "SKU-001", unitPrice: 500m, quantity: 2),
                CreateValidItem(sku: "SKU-002", unitPrice: 250m, quantity: 1)
            }
        };

        // Act
        var createResponse = await _client.PostAsJsonAsync("/api/orders", request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(createResponse.Headers.Location);

        var created = await createResponse.Content.ReadFromJsonAsync<OrderResponse>();

        var getResponse = await _client.GetAsync(createResponse.Headers.Location);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var retrieved = await getResponse.Content.ReadFromJsonAsync<OrderResponse>();

        Assert.NotNull(created);
        Assert.NotNull(retrieved);
        Assert.Equal(created.Id, retrieved.Id);
        Assert.Equal(customer.Id, retrieved.CustomerId);
        Assert.Equal("ZAR", retrieved.Currency);
        Assert.Equal(1250m, retrieved.Total);
        Assert.Equal(2, retrieved.Items.Count);
    }

    // ---------- Validation failures ----------

    [Fact]
    public async Task CreateOrder_WithManyInvalidFields_ReturnsOneErrorPerField()
    {
        // Arrange
        var request = new
        {
            currency = "ZA",
            status = "",
            items = new[]
            {
                CreateValidItem(sku: "", unitPrice: -1m, quantity: 0)
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/orders", request);

        // Assert
        var problem = await ReadValidationProblemAsync(response);

        Assert.Equal(
            new[]
            {
                "Currency",
                "CustomerId",
                "Items[0].Quantity",
                "Items[0].Sku",
                "Items[0].UnitPrice",
                "Status"
            },
            problem.Errors.Keys.Order());

        Assert.All(problem.Errors.Values, messages => Assert.Single(messages));
    }

    [Fact]
    public async Task CreateOrder_WithMissingProperties_ReturnsFieldLevelErrors()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/orders", new { customerId = 1 });

        // Assert
        var problem = await ReadValidationProblemAsync(response);

        Assert.Contains("Currency", problem.Errors.Keys);
        Assert.Contains("Status", problem.Errors.Keys);
        Assert.Contains("Items", problem.Errors.Keys);
    }

    [Fact]
    public async Task CreateOrder_WithNullItemEntry_ReturnsErrorForThatEntry()
    {
        // Arrange
        var request = new
        {
            customerId = 1,
            currency = "ZAR",
            status = "Created",
            items = new[] { CreateValidItem(), null }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/orders", request);

        // Assert
        var problem = await ReadValidationProblemAsync(response);

        Assert.Equal(new[] { "Order item must not be null." }, problem.Errors["Items[1]"]);
    }

    [Fact]
    public async Task CreateOrder_WithNullDescription_ReturnsDescriptionError()
    {
        // Arrange
        var request = new
        {
            customerId = 1,
            currency = "ZAR",
            status = "Created",
            items = new[]
            {
                new { sku = "SKU-001", description = (string?)null, unitPrice = 1m, quantity = 1 }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/orders", request);

        // Assert
        var problem = await ReadValidationProblemAsync(response);

        Assert.Contains("Items[0].Description", problem.Errors.Keys);
    }

    [Fact]
    public async Task CreateOrder_WithFieldsOverDatabaseLimits_ReturnsBadRequestNotServerError()
    {
        // Arrange
        var customer = await AddCustomerAsync();

        var request = new
        {
            customerId = customer.Id,
            currency = "ZAR",
            status = new string('S', CreateOrderRequestValidator.StatusMaxLength + 1),
            items = new[]
            {
                CreateValidItem(
                    sku: new string('S', CreateOrderItemRequestValidator.SkuMaxLength + 1),
                    description: new string('D', CreateOrderItemRequestValidator.DescriptionMaxLength + 1),
                    unitPrice: 10.999m)
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/orders", request);

        // Assert
        var problem = await ReadValidationProblemAsync(response);

        Assert.Contains("Status", problem.Errors.Keys);
        Assert.Contains("Items[0].Sku", problem.Errors.Keys);
        Assert.Contains("Items[0].Description", problem.Errors.Keys);
        Assert.Contains("Items[0].UnitPrice", problem.Errors.Keys);
    }

    [Fact]
    public async Task CreateOrder_WithInvalidRequest_DoesNotPersistOrder()
    {
        // Arrange
        var customer = await AddCustomerAsync();

        var request = new
        {
            customerId = customer.Id,
            currency = "ZAR",
            status = "Created",
            items = new[] { CreateValidItem(quantity: 0) }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/orders", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountOrdersAsync());
    }

    // ---------- Missing resources ----------

    [Fact]
    public async Task CreateOrder_WhenCustomerDoesNotExist_ReturnsNotFound()
    {
        // Arrange
        var request = new
        {
            legacyCustomerId = "9999999999",
            currency = "ZAR",
            status = "Created",
            items = new[] { CreateValidItem() }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/orders", request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountOrdersAsync());
    }

    [Fact]
    public async Task GetOrderById_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync("/api/orders/999");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
