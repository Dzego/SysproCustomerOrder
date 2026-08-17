using Microsoft.AspNetCore.Mvc;
using Syspro.Api.Services;

namespace Syspro.Api.Controllers;

[ApiController]
[Route("api/import")]
public class ImportController : ControllerBase
{
    private readonly ICustomerImportService _customerImportService;
    private readonly IWebHostEnvironment _environment;

    public ImportController(
        ICustomerImportService customerImportService,
        IWebHostEnvironment environment)
    {
        _customerImportService = customerImportService;
        _environment = environment;
    }

    [HttpPost("customers")]
    public async Task<IActionResult> ImportCustomers()
    {
         var filePath = Path.Combine(
            _environment.ContentRootPath,
            "LegacyData",
            "customers_legacy.dat");

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new
            {
                message = "Legacy customer file was not found."
            });
        }

        var result = await _customerImportService.ImportAsync(filePath);

        return Ok(result);
    }
}