using Syspro.Api.DTOs;
using Syspro.Api.Services;

namespace Syspro.IntegrationTests.Infrastructure;

// Runs the given behaviour for a lookup, which is expected to throw.
public sealed class StubOrderService(Func<CancellationToken, Task> getById) : IOrderService
{
    public Task<OrderResponse> CreateAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public async Task<OrderResponse?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        await getById(cancellationToken);

        throw new InvalidOperationException("The behaviour was expected to throw.");
    }

    public Task<List<CustomerOrderTotalResponse>> GetCustomerTotalsAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}
