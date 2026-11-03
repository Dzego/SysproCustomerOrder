using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Syspro.Api.Controllers;
using Syspro.Api.DTOs;
using Syspro.Api.Services;
using Syspro.Api.Validators;
using Xunit;

namespace Syspro.Tests.Controllers;

public class ControllerCancellationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static CreateOrderRequest CreateValidRequest()
    {
        return new CreateOrderRequest
        {
            CustomerId = 1,
            Currency = "ZAR",
            Status = "Created",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "SKU-001",
                    Description = "Keyboard",
                    UnitPrice = 500m,
                    Quantity = 1
                }
            ]
        };
    }

    private static OrdersController CreateOrdersController(IOrderService orderService)
    {
        return new OrdersController(
            orderService,
            new CreateOrderRequestValidator(new CreateOrderItemRequestValidator()));
    }

    // Records the token it receives, then fails the way a cancelled query would.
    private sealed class CancellingOrderService : IOrderService
    {
        public CancellationToken? ReceivedToken { get; private set; }

        public Task<OrderResponse> CreateAsync(
            CreateOrderRequest request,
            CancellationToken cancellationToken)
        {
            return Cancel<OrderResponse>(cancellationToken);
        }

        public Task<OrderResponse?> GetByIdAsync(int id, CancellationToken cancellationToken)
        {
            return Cancel<OrderResponse?>(cancellationToken);
        }

        public Task<List<CustomerOrderTotalResponse>> GetCustomerTotalsAsync(
            DateTime fromDate,
            DateTime toDate,
            CancellationToken cancellationToken)
        {
            return Cancel<List<CustomerOrderTotalResponse>>(cancellationToken);
        }

        private Task<T> Cancel<T>(CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;

            throw new OperationCanceledException(cancellationToken);
        }
    }

    // Blocks until the token passed by the controller is cancelled.
    private sealed class BlockingServices : IOrderService, ICustomerImportService
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OrderResponse> CreateAsync(
            CreateOrderRequest request,
            CancellationToken cancellationToken)
        {
            return BlockAsync<OrderResponse>(cancellationToken);
        }

        public Task<OrderResponse?> GetByIdAsync(int id, CancellationToken cancellationToken)
        {
            return BlockAsync<OrderResponse?>(cancellationToken);
        }

        public Task<List<CustomerOrderTotalResponse>> GetCustomerTotalsAsync(
            DateTime fromDate,
            DateTime toDate,
            CancellationToken cancellationToken)
        {
            return BlockAsync<List<CustomerOrderTotalResponse>>(cancellationToken);
        }

        public Task<ImportResult> ImportAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            return BlockAsync<ImportResult>(cancellationToken);
        }

        private async Task<T> BlockAsync<T>(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();

                throw;
            }

            throw new InvalidOperationException("The token was never cancelled.");
        }
    }

    private sealed class BlockingApiFactory(BlockingServices services)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // The default Development environment would load user secrets.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(serviceCollection =>
            {
                serviceCollection.RemoveAll<IOrderService>();
                serviceCollection.RemoveAll<ICustomerImportService>();

                serviceCollection.AddSingleton<IOrderService>(services);
                serviceCollection.AddSingleton<ICustomerImportService>(services);
            });
        }
    }

    // Goes through the real pipeline to prove the framework's request-aborted
    // token is what reaches the service for every endpoint.
    [Theory]
    [InlineData("POST", "/api/orders")]
    [InlineData("GET", "/api/orders/1")]
    [InlineData("GET", "/api/customers/totals?fromDate=2026-01-01&toDate=2026-12-31")]
    [InlineData("POST", "/api/import/customers")]
    public async Task Endpoint_WhenClientDisconnects_CancelsServiceCall(
        string method,
        string path)
    {
        // Arrange
        var services = new BlockingServices();

        using var factory = new BlockingApiFactory(services);
        using var client = factory.CreateClient();
        using var cancellationSource = new CancellationTokenSource();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (path == "/api/orders")
        {
            request.Content = JsonContent.Create(CreateValidRequest());
        }

        // Act
        var sendTask = client.SendAsync(request, cancellationSource.Token);

        await services.Entered.Task.WaitAsync(TestTimeout);

        cancellationSource.Cancel();

        // Assert
        await services.Cancelled.Task.WaitAsync(TestTimeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
    }

    [Fact]
    public async Task CreateOrder_WhenServiceIsCancelled_PropagatesExceptionAndToken()
    {
        // Arrange
        var orderService = new CancellingOrderService();
        var controller = CreateOrdersController(orderService);

        using var cancellationSource = new CancellationTokenSource();

        // Act
        var action = () => controller.CreateOrder(
            CreateValidRequest(),
            cancellationSource.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Equal(cancellationSource.Token, orderService.ReceivedToken);
    }

    [Fact]
    public async Task GetOrderById_WhenServiceIsCancelled_PropagatesExceptionAndToken()
    {
        // Arrange
        var orderService = new CancellingOrderService();
        var controller = CreateOrdersController(orderService);

        using var cancellationSource = new CancellationTokenSource();

        // Act
        var action = () => controller.GetOrderById(1, cancellationSource.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Equal(cancellationSource.Token, orderService.ReceivedToken);
    }

    [Fact]
    public async Task GetCustomerTotals_WhenServiceIsCancelled_PropagatesExceptionAndToken()
    {
        // Arrange
        var orderService = new CancellingOrderService();
        var controller = new CustomersController(orderService);

        using var cancellationSource = new CancellationTokenSource();

        // Act
        var action = () => controller.GetCustomerTotals(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            cancellationSource.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Equal(cancellationSource.Token, orderService.ReceivedToken);
    }

    // The request is invalid, so a validator that ignored the token would
    // return a 400 instead of cancelling.
    [Fact]
    public async Task CreateOrder_WhenCancelledDuringValidation_DoesNotReturnBadRequest()
    {
        // Arrange
        var orderService = new CancellingOrderService();
        var controller = CreateOrdersController(orderService);

        var invalidRequest = new CreateOrderRequest();

        // Act
        var action = () => controller.CreateOrder(
            invalidRequest,
            new CancellationToken(canceled: true));

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Null(orderService.ReceivedToken);
    }
}
