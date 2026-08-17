using Syspro.Api.Models;

namespace Syspro.Api.Repositories;

public interface IImportRepository
{
    Task AddLogAsync(ImportLog importLog);
    Task SaveChangesAsync();
}