using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Syspro.Api.DTOs;
using Microsoft.Extensions.Options;
using Syspro.Api.Models;
using Syspro.Api.Services;

namespace Syspro.IntegrationTests.Infrastructure;

public static class TestData
{
    public static Customer Customer(string legacyCustomerId = "0000012345", string name = "John Smith")
    {
        return new Customer
        {
            LegacyCustomerId = legacyCustomerId,
            Name = name,
            Email = "john.smith@example.com",
            Tier = "A",
            SignupDate = new DateOnly(2021, 3, 15)
        };
    }

    public static Order Order(int customerId, DateTime? orderDate = null, params OrderItem[] items)
    {
        return new Order
        {
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 6, 15, 12, 0, 0),
            Currency = "ZAR",
            Status = "Created",
            Items = items
        };
    }

    public static OrderItem Item(decimal unitPrice = 100m, int quantity = 1, string sku = "SKU-001")
    {
        return new OrderItem
        {
            Sku = sku,
            Description = "Keyboard",
            UnitPrice = unitPrice,
            Quantity = quantity
        };
    }

    public static CreateOrderRequest OrderRequest(
        int? customerId = null,
        string? legacyCustomerId = null,
        params CreateOrderItemRequest[] items)
    {
        return new CreateOrderRequest
        {
            CustomerId = customerId,
            LegacyCustomerId = legacyCustomerId,
            Currency = "zar",
            Status = "Created",
            Items = items.Length > 0 ? [.. items] : [ItemRequest()]
        };
    }

    public static CreateOrderItemRequest ItemRequest(
        string sku = "SKU-001",
        string description = "Keyboard",
        decimal unitPrice = 500m,
        int quantity = 2)
    {
        return new CreateOrderItemRequest
        {
            Sku = sku,
            Description = description,
            UnitPrice = unitPrice,
            Quantity = quantity
        };
    }

    // Builds an 80-character record in the legacy fixed-width layout.
    public static string LegacyLine(
        string legacyCustomerId = "0000012345",
        string name = "John Smith",
        string email = "john.smith@example.com",
        string signupDate = "20210315",
        string tier = "A")
    {
        return legacyCustomerId.PadRight(10) +
               name.PadRight(30) +
               email.PadRight(30) +
               signupDate.PadRight(8) +
               tier.PadRight(2);
    }

    public static IOptions<CustomerImportOptions> ImportOptions(int? batchSize = null, TimeSpan? leaseDuration = null)
    {
        var options = new CustomerImportOptions();

        options.BatchSize = batchSize ?? options.BatchSize;
        options.LeaseDuration = leaseDuration ?? options.LeaseDuration;

        return Options.Create(options);
    }

    // The bytes File.WriteAllLines would produce for these lines.
    public static byte[] ToInput(params string[] lines)
    {
        return Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n")));
    }

    public static int SqlErrorNumber(DbUpdateException exception)
    {
        return Assert.IsType<SqlException>(exception.InnerException).Number;
    }
}
