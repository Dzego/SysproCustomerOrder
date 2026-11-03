using Syspro.Api.DTOs;

namespace Syspro.Api.Services;

public interface IOrderService
{
    Task<OrderResponse> CreateAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken);

    Task<OrderResponse?> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<List<CustomerOrderTotalResponse>> GetCustomerTotalsAsync(
    DateTime fromDate,
    DateTime toDate,
    CancellationToken cancellationToken);
}