namespace Syspro.Api.DTOs;

// Members default instead of using 'required' so that missing JSON properties
// reach CreateOrderRequestValidator and are reported as field-level errors.
public sealed class CreateOrderRequest
{
    public int? CustomerId { get; init; }

    public string? LegacyCustomerId { get; init; }

    public string Currency { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public List<CreateOrderItemRequest> Items { get; init; } = [];
}

public sealed class CreateOrderItemRequest
{
    public string Sku { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public decimal UnitPrice { get; init; }

    public int Quantity { get; init; }
}
