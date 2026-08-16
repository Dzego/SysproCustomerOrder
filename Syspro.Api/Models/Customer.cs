namespace Syspro.Api.Models;

public class Customer
{
    public int Id { get; set; }

    public required string LegacyCustomerId { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    public required string Tier { get; set; }

    public DateOnly SignupDate { get; set; }

    public ICollection<Order> Orders { get; set; } = [];
}