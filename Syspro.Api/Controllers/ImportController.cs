using Microsoft.AspNetCore.Mvc;
using Syspro.Api.Services;

namespace Syspro.Api.Controllers;

[ApiController]
[Route("api/import")]
public class ImportController : ControllerBase
{
    private readonly ICustomerImportJobService _importJobService;

    public ImportController(ICustomerImportJobService importJobService)
    {
        _importJobService = importJobService;
    }

    [HttpPost("customers")]
    public async Task<IActionResult> ImportCustomers(CancellationToken cancellationToken)
    {
        var job = await _importJobService.ScheduleAsync(cancellationToken);

        return AcceptedAtAction(nameof(GetImportJob), new { id = job.Id }, job);
    }

    [HttpGet("jobs/{id:int}")]
    public async Task<IActionResult> GetImportJob(int id, CancellationToken cancellationToken)
    {
        var job = await _importJobService.GetAsync(id, cancellationToken);

        if (job is null)
        {
            return Problem(
                detail: $"Import job with id {id} was not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(job);
    }
}
