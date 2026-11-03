using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

public interface ICustomerRepository
{
    Task<Customer?> GetByLegacyCustomerIdAsync(
        string legacyCustomerId,
        CancellationToken cancellationToken);

    Task AddAsync(Customer customer, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}