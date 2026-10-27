using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Syspro.Api.Data;
using Syspro.Api.Models;
using Xunit;

namespace Syspro.Tests.Controllers;

public class OrdersControllerTests : IClassFixture<OrdersControllerTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public OrdersControllerTests(ApiFactory factory)
    {
        _factory = factory;
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(
            Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();

                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(_databaseName));
            });
        }
    }

    private async Task<Customer> AddCustomerAsync(string legacyCustomerId)
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

    [Fact]
    public async Task CreateOrder_WithValidRequest_ReturnsCreated()
    {
        // Arrange
        var customer = await AddCustomerAsync("0000020001");
        var client = _factory.CreateClient();

        var request = new
        {
            legacyCustomerId = customer.LegacyCustomerId,
            currency = "zar",
            status = "Created",
            items = new[]
            {
                new { sku = "SKU-001", description = "Keyboard", unitPrice = 500m, quantity = 2 }
            }
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/orders", request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task CreateOrder_WithInvalidFields_ReturnsFieldLevelValidationProblem()
    {
        // Arrange
        var client = _factory.CreateClient();

        var request = new
        {
            currency = "ZA",
            status = "Created",
            items = new[]
            {
                new { sku = "", description = "Keyboard", unitPrice = -1m, quantity = 0 }
            }
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/orders", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>();

        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.Contains("CustomerId", problem.Errors.Keys);
        Assert.Contains("Currency", problem.Errors.Keys);
        Assert.Contains("Items[0].Sku", problem.Errors.Keys);
        Assert.Contains("Items[0].UnitPrice", problem.Errors.Keys);
        Assert.Contains("Items[0].Quantity", problem.Errors.Keys);
    }

    [Fact]
    public async Task CreateOrder_WithMissingProperties_ReturnsFieldLevelValidationProblem()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(
            "/api/orders",
            new { customerId = 1 });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>();

        Assert.NotNull(problem);
        Assert.Contains("Currency", problem.Errors.Keys);
        Assert.Contains("Status", problem.Errors.Keys);
        Assert.Contains("Items", problem.Errors.Keys);
    }

    [Fact]
    public async Task CreateOrder_WhenCustomerDoesNotExist_ReturnsNotFound()
    {
        // Arrange
        var client = _factory.CreateClient();

        var request = new
        {
            legacyCustomerId = "9999999999",
            currency = "ZAR",
            status = "Created",
            items = new[]
            {
                new { sku = "SKU-001", description = "Keyboard", unitPrice = 500m, quantity = 1 }
            }
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/orders", request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
