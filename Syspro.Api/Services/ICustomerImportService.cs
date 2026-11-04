using Syspro.Api.DTOs;
using Syspro.Api.Models;

namespace Syspro.Api.Services;

public interface ICustomerImportService
{
    // Writes the customers, row errors and the job's Completed status, and removes
    // the job's stored input, in one transaction. A job that is not Completed has
    // therefore changed nothing and still has its input for a retry.
    Task<ImportResult> ImportAsync(
        ImportLog job,
        Stream input,
        CancellationToken cancellationToken);
}
