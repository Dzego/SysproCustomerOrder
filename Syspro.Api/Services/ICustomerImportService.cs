using Syspro.Api.DTOs;
using Syspro.Api.Models;

namespace Syspro.Api.Services;

public interface ICustomerImportService
{
    // Writes the customers, row errors and the job's Completed status in one
    // transaction, so a job that is not Completed has changed nothing.
    Task<ImportResult> ImportAsync(
        ImportLog job,
        string filePath,
        CancellationToken cancellationToken);
}
