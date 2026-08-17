namespace Syspro.Api.DTOs;

public sealed class CustomerOrderTotalResponse
{
    public int CustomerId { get; init; }

    public required string LegacyCustomerId { get; init; }

    public required string Name { get; init; }

    public decimal Total { get; init; }
}