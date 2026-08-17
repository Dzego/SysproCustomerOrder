using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.Models;

namespace Syspro.Api.Services;

public sealed class OrderService : IOrderService
{
    private readonly AppDbContext _dbContext;

    public OrderService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OrderResponse> CreateAsync(CreateOrderRequest request)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ArgumentException("An order must contain at least one item.");
        }

        if (string.IsNullOrWhiteSpace(request.Currency) ||
            request.Currency.Length != 3)
        {
            throw new ArgumentException("Currency must be a 3-character code.");
        }

        Customer? customer;

        if (request.CustomerId.HasValue)
        {
            customer = await _dbContext.Customers
                .FirstOrDefaultAsync(x => x.Id == request.CustomerId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(request.LegacyCustomerId))
        {
            customer = await _dbContext.Customers
                .FirstOrDefaultAsync(
                    x => x.LegacyCustomerId == request.LegacyCustomerId);
        }
        else
        {
            throw new ArgumentException(
                "CustomerId or LegacyCustomerId must be provided.");
        }

        if (customer is null)
        {
            throw new KeyNotFoundException("Customer was not found.");
        }

        foreach (var item in request.Items)
        {
            if (item.Quantity <= 0)
            {
                throw new ArgumentException(
                    "Item quantity must be greater than zero.");
            }

            if (item.UnitPrice < 0)
            {
                throw new ArgumentException(
                    "Item unit price cannot be negative.");
            }

            if (string.IsNullOrWhiteSpace(item.Sku))
            {
                throw new ArgumentException("Item SKU is required.");
            }
        }

        var order = new Order
        {
            CustomerId = customer.Id,
            OrderDate = DateTime.UtcNow,
            Currency = request.Currency.ToUpperInvariant(),
            Status = request.Status,
            Items = request.Items.Select(item => new OrderItem
            {
                Sku = item.Sku,
                Description = item.Description,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity
            }).ToList()
        };

        _dbContext.Orders.Add(order);

        await _dbContext.SaveChangesAsync();

        return MapToResponse(order);
    }

    public async Task<OrderResponse?> GetByIdAsync(int id)
    {
        var order = await _dbContext.Orders
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id);

        return order is null
            ? null
            : MapToResponse(order);
    }

    private static OrderResponse MapToResponse(Order order)
    {
        var items = order.Items
            .Select(item => new OrderItemResponse
            {
                Id = item.Id,
                Sku = item.Sku,
                Description = item.Description,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                LineTotal = item.UnitPrice * item.Quantity
            })
            .ToList();

        return new OrderResponse
        {
            Id = order.Id,
            CustomerId = order.CustomerId,
            OrderDate = order.OrderDate,
            Currency = order.Currency,
            Status = order.Status,
            Total = items.Sum(x => x.LineTotal),
            Items = items
        };
    }

    public async Task<List<CustomerOrderTotalResponse>> GetCustomerTotalsAsync(
    DateTime fromDate,
    DateTime toDate)
    {
        if (fromDate > toDate)
        {
            throw new ArgumentException(
                "fromDate cannot be later than toDate.");
        }

        return await _dbContext.Customers
            .Select(customer => new CustomerOrderTotalResponse
            {
                CustomerId = customer.Id,
                LegacyCustomerId = customer.LegacyCustomerId,
                Name = customer.Name,

                Total = customer.Orders
                    .Where(order =>
                        order.OrderDate >= fromDate &&
                        order.OrderDate <= toDate)
                    .SelectMany(order => order.Items)
                    .Sum(item => (decimal?)
                        (item.UnitPrice * item.Quantity)) ?? 0
            })
            .OrderByDescending(x => x.Total)
            .ToListAsync();
    }
}