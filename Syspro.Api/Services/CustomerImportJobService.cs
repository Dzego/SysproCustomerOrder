using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Repositories;

namespace Syspro.Api.Services;

public sealed class CustomerImportJobService : ICustomerImportJobService
{
    private readonly IImportRepository _importRepository;

    public CustomerImportJobService(IImportRepository importRepository)
    {
        _importRepository = importRepository;
    }

    // The token only covers saving the job. Once saved, the job is processed with
    // the worker's own token, so a client disconnect cannot affect it.
    public async Task<ImportJobResponse> ScheduleAsync(CancellationToken cancellationToken)
    {
        var job = new ImportLog
        {
            Status = ImportJobStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        await _importRepository.AddLogAsync(job, cancellationToken);
        await _importRepository.SaveChangesAsync(cancellationToken);

        return ImportJobResponse.FromJob(job);
    }

    public async Task<ImportJobResponse?> GetAsync(int id, CancellationToken cancellationToken)
    {
        var job = await _importRepository.GetJobAsync(id, cancellationToken);

        return job is null ? null : ImportJobResponse.FromJob(job);
    }
}
