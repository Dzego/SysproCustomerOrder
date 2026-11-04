using Syspro.Api.DTOs;
using Syspro.Api.Models;

namespace Syspro.Api.Services;

public interface ICustomerImportService
{
    // Resumes after the job's checkpoint, and commits one transaction per batch: customers,
    // row errors, counters and the checkpoint together. The last batch also completes the
    // job and removes its stored input. A job that is not Completed can have earlier
    // batches applied, and still has its input so that a retry can resume.
    Task<ImportResult> ImportAsync(
        ImportLog job,
        Stream input,
        CancellationToken cancellationToken);
}
