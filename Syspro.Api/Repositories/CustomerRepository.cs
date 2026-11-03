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

    public Task<Customer?> GetByLegacyCustomerIdAsync(
        string legacyCustomerId,
        CancellationToken cancellationToken)
    {
        return _dbContext.Customers
            .FirstOrDefaultAsync(
                customer => customer.LegacyCustomerId == legacyCustomerId,
                cancellationToken);
    }

    public async Task AddAsync(Customer customer, CancellationToken cancellationToken)
    {
        await _dbContext.Customers.AddAsync(customer, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }
}