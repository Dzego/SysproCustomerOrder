namespace Syspro.Api.Services;

public interface ICustomerImportJobRunner
{
    Task RecoverInterruptedJobsAsync(CancellationToken stoppingToken);

    // Returns false when no job was waiting.
    Task<bool> RunNextJobAsync(CancellationToken stoppingToken);
}
