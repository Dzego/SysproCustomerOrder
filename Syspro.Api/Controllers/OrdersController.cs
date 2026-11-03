using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Syspro.Api.DTOs;
using Syspro.Api.Services;
using Syspro.Api.Validators;

namespace Syspro.Api.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;
    private readonly IValidator<CreateOrderRequest> _createOrderValidator;

    public OrdersController(
        IOrderService orderService,
        IValidator<CreateOrderRequest> createOrderValidator)
    {
        _orderService = orderService;
        _createOrderValidator = createOrderValidator;
    }

    [HttpPost]
    public async Task<IActionResult> CreateOrder(
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        var validationResult = await _createOrderValidator.ValidateAsync(
            request,
            cancellationToken);

        if (!validationResult.IsValid)
        {
            validationResult.AddToModelState(ModelState);

            return ValidationProblem(ModelState);
        }

        var result = await _orderService.CreateAsync(request, cancellationToken);

        return CreatedAtAction(
            nameof(GetOrderById),
            new { id = result.Id },
            result);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOrderById(
        int id,
        CancellationToken cancellationToken)
    {
        var result = await _orderService.GetByIdAsync(id, cancellationToken);

        if (result is null)
        {
            return Problem(
                detail: $"Order with id {id} was not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(result);
    }
}