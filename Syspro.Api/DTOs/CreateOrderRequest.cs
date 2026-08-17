namespace Syspro.Api.DTOs;

public sealed class CreateOrderRequest
{
    public int? CustomerId { get; init; }

    public string? LegacyCustomerId { get; init; }

    public required string Currency { get; init; }

    public required string Status { get; init; }

    public required List<CreateOrderItemRequest> Items { get; init; }
}

public sealed class CreateOrderItemRequest
{
    public required string Sku { get; init; }

    public required string Description { get; init; }

    public decimal UnitPrice { get; init; }

    public int Quantity { get; init; }
}