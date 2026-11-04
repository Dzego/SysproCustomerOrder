using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

public class CustomerRepository : ICustomerRepository
{
    private readonly AppDbContext _dbContext;

    public CustomerRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Dictionary<string, Customer>> GetByLegacyCustomerIdsAsync(
        IReadOnlyCollection<string> legacyCustomerIds,
        CancellationToken cancellationToken)
    {
        if (legacyCustomerIds.Count == 0)
        {
            return new Dictionary<string, Customer>(StringComparer.OrdinalIgnoreCase);
        }

        return await _dbContext.Customers
            .Where(customer => legacyCustomerIds.Contains(customer.LegacyCustomerId))
            .ToDictionaryAsync(
                customer => customer.LegacyCustomerId,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);
    }

    public void Add(Customer customer)
    {
        _dbContext.Customers.Add(customer);
    }
}
