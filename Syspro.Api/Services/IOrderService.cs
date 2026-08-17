using Syspro.Api.DTOs;

namespace Syspro.Api.Services;

public interface IOrderService
{
    Task<OrderResponse> CreateAsync(CreateOrderRequest request);
    Task<OrderResponse?> GetByIdAsync(int id);

    Task<List<CustomerOrderTotalResponse>> GetCustomerTotalsAsync(
    DateTime fromDate,
    DateTime toDate);
}