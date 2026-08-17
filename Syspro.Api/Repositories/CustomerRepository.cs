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

    public Task<Customer?> GetByLegacyCustomerIdAsync(string legacyCustomerId)
    {
        return _dbContext.Customers
            .FirstOrDefaultAsync(
                customer => customer.LegacyCustomerId == legacyCustomerId);
    }

    public async Task AddAsync(Customer customer)
    {
        await _dbContext.Customers.AddAsync(customer);
    }

    public Task SaveChangesAsync()
    {
        return _dbContext.SaveChangesAsync();
    }
}