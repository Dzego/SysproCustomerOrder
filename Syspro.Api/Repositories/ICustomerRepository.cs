using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

public interface ICustomerRepository
{
    Task<Customer?> GetByLegacyCustomerIdAsync(string legacyCustomerId);

    Task AddAsync(Customer customer);

    Task SaveChangesAsync();
}