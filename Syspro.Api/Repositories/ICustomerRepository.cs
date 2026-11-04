using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

public interface ICustomerRepository
{
    // Keys ignore case, matching the unique index under SQL Server's default collation.
    // The returned customers are tracked, so changes to them are saved with the batch.
    Task<Dictionary<string, Customer>> GetByLegacyCustomerIdsAsync(
        IReadOnlyCollection<string> legacyCustomerIds,
        CancellationToken cancellationToken);

    void Add(Customer customer);
}
