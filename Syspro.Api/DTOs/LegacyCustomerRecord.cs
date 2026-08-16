namespace Syspro.Api.DTOs;

public sealed class LegacyCustomerRecord
{
    public required string LegacyCustomerId { get; init; }

    public required string Name { get; init; }

    public required string Email { get; init; }

    public DateOnly SignupDate { get; init; }

    public required string Tier { get; init; }
}