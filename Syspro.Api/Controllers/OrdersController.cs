using Microsoft.AspNetCore.Mvc;
using Syspro.Api.DTOs;
using Syspro.Api.Services;

namespace Syspro.Api.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrdersController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateOrder(CreateOrderRequest request)
    {
        try
        {
            var result = await _orderService.CreateAsync(request);

            return CreatedAtAction(
                nameof(GetOrderById),
                new { id = result.Id },
                result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new
            {
                message = ex.Message
            });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOrderById(int id)
    {
        var result = await _orderService.GetByIdAsync(id);

        if (result is null)
        {
            return NotFound(new
            {
                message = $"Order with id {id} was not found."
            });
        }

        return Ok(result);
    }
}