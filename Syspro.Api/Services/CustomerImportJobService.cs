using Microsoft.Extensions.Options;
using Syspro.Api.DTOs;
using Syspro.Api.Models;
using Syspro.Api.Repositories;

namespace Syspro.Api.Services;

public sealed class CustomerImportJobService : ICustomerImportJobService
{
    private readonly IImportRepository _importRepository;
    private readonly IHostEnvironment _environment;
    private readonly CustomerImportOptions _options;

    public CustomerImportJobService(
        IImportRepository importRepository,
        IHostEnvironment environment,
        IOptions<CustomerImportOptions> options)
    {
        _importRepository = importRepository;
        _environment = environment;
        _options = options.Value;
    }

    // Saves the job and a copy of its input in one transaction, so the job can never
    // exist without its input. The token only covers this step. Once saved, the job
    // is processed with the worker's own token, so a client disconnect cannot affect it.
    public async Task<ImportJobResponse> ScheduleAsync(CancellationToken cancellationToken)
    {
        var job = new ImportLog
        {
            Status = ImportJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            Input = new ImportJobInput
            {
                Content = await ReadInputAsync(cancellationToken)
            }
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

    private async Task<byte[]> ReadInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await LegacyCustomerFile.ReadAsync(
                _environment,
                _options.MaxInputBytes,
                cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ResourceNotFoundException("Legacy customer file was not found.");
        }
    }
}
