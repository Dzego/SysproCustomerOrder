namespace Syspro.Api.Models;

public class Order
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public DateTime OrderDate { get; set; }

    public required string Currency { get; set; }

    public required string Status { get; set; }

    public Customer Customer { get; set; } = null!;

    public ICollection<OrderItem> Items { get; set; } = [];
}