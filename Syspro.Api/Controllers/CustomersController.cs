using Microsoft.AspNetCore.Mvc;
using Syspro.Api.Services;

namespace Syspro.Api.Controllers;

[ApiController]
[Route("api/customers")]
public sealed class CustomersController : ControllerBase
{
    private readonly IOrderService _orderService;

    public CustomersController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpGet("totals")]
    public async Task<IActionResult> GetCustomerTotals(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate)
    {
        try
        {
            var result = await _orderService
                .GetCustomerTotalsAsync(fromDate, toDate);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }
}