using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

public interface IImportRepository
{
    Task AddLogAsync(ImportLog importLog, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}