using Syspro.Api.DTOs;

namespace Syspro.Api.Services;

public interface ICustomerImportJobService
{
    Task<ImportJobResponse> ScheduleAsync(CancellationToken cancellationToken);

    Task<ImportJobResponse?> GetAsync(int id, CancellationToken cancellationToken);
}
