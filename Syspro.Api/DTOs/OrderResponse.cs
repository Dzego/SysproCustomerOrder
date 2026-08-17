namespace Syspro.Api.DTOs;

public sealed class OrderResponse
{
    public int Id { get; init; }

    public int CustomerId { get; init; }

    public DateTime OrderDate { get; init; }

    public required string Currency { get; init; }

    public required string Status { get; init; }

    public decimal Total { get; init; }

    public required List<OrderItemResponse> Items { get; init; }
}

public sealed class OrderItemResponse
{
    public int Id { get; init; }

    public required string Sku { get; init; }

    public required string Description { get; init; }

    public decimal UnitPrice { get; init; }

    public int Quantity { get; init; }

    public decimal LineTotal { get; init; }
}