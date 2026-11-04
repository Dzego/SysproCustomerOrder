namespace Syspro.Api.Services;

public interface ICustomerImportJobRunner
{
    // Returns false when no job could be claimed.
    Task<bool> RunNextJobAsync(CancellationToken stoppingToken);
}
