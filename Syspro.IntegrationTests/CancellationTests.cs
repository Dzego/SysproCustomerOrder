using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Syspro.Api.ExceptionHandling;
using Syspro.Api.Services;
using Syspro.IntegrationTests.Infrastructure;

namespace Syspro.IntegrationTests;

// SqlClient reports a command cancelled while it is running as either a
// SqlException or an OperationCanceledException, so these tests cancel commands
// SQL Server is really executing instead of assuming the exception type.
public sealed class CancellationTests(SqlServerFixture fixture) : SqlServerTestBase(fixture)
{
    private const string HandlerCategory =
        "Syspro.Api.ExceptionHandling.GlobalExceptionHandler";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CancelDelay = TimeSpan.FromMilliseconds(500);

    private async Task<Exception> CancelRunningLookupAsync()
    {
        using var cancellationSource = new CancellationTokenSource(CancelDelay);

        await using var dbContext = CreateDbContext(new SlowQueryInterceptor());

        return await Assert.ThrowsAnyAsync<Exception>(
            () => new OrderService(dbContext).GetByIdAsync(1, cancellationSource.Token));
    }

    private static async Task DisconnectDuringRequestAsync(
        ApiFactory factory,
        Task serverEntered)
    {
        using var client = factory.CreateClient();
        using var cancellationSource = new CancellationTokenSource();

        var sendTask = client.GetAsync("/api/orders/1", cancellationSource.Token);

        await serverEntered.WaitAsync(TestTimeout);

        // Gives SQL Server time to begin executing the command before cancelling.
        await Task.Delay(CancelDelay);

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
    }

    // ---------- Classifying real SqlClient exceptions ----------

    [SqlServerFact]
    public async Task RunningQuery_WhenCancelled_IsRecognizedAsCancellation()
    {
        var exception = await CancelRunningLookupAsync();

        Assert.True(
            CancellationExceptionClassifier.IsCancellation(exception),
            $"Not recognized as a cancellation: {exception}");
    }

    [SqlServerFact]
    public async Task RunningSave_WhenCancelled_IsRecognizedAndPersistsNothing()
    {
        var customer = await AddCustomerAsync();

        using var cancellationSource = new CancellationTokenSource();

        await using var dbContext = CreateDbContext(new SlowQueryInterceptor("INSERT"));

        cancellationSource.CancelAfter(CancelDelay);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new OrderService(dbContext).CreateAsync(
                TestData.OrderRequest(customerId: customer.Id),
                cancellationSource.Token));

        Assert.True(
            CancellationExceptionClassifier.IsCancellation(exception),
            $"Not recognized as a cancellation: {exception}");
        Assert.Equal(0, await QueryAsync(context => context.Orders.CountAsync()));
    }

    [SqlServerFact]
    public async Task InvalidSql_IsNotRecognizedAsCancellation()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TableThatDoesNotExist";

        var exception = await Assert.ThrowsAsync<SqlException>(
            () => command.ExecuteReaderAsync());

        Assert.False(CancellationExceptionClassifier.IsCancellation(exception));
    }

    [SqlServerFact]
    public async Task CommandTimeout_IsNotRecognizedAsCancellation()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "WAITFOR DELAY '00:00:30'";
        command.CommandTimeout = 1;

        var exception = await Assert.ThrowsAsync<SqlException>(
            () => command.ExecuteNonQueryAsync());

        Assert.False(CancellationExceptionClassifier.IsCancellation(exception));
    }

    // ---------- Through the API ----------

    [SqlServerFact]
    public async Task ClientDisconnect_DuringRunningQuery_IsNotAServerError()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var factory = new ApiFactory(
            ConnectionString,
            interceptors: [new SlowQueryInterceptor(), new SignalOnQueryInterceptor(entered)]);

        await DisconnectDuringRequestAsync(factory, entered.Task);

        var statusCode = await factory.Outcome.StatusCode.Task.WaitAsync(TestTimeout);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, statusCode);
        Assert.DoesNotContain(
            factory.Logs.Entries,
            entry => entry.Category == HandlerCategory && entry.Level >= LogLevel.Warning);
    }

    // A real SQL failure must stay visible even though the client has also gone.
    [SqlServerFact]
    public async Task ClientDisconnect_WithRealSqlFailure_IsStillLoggedAsAnError()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var orderService = new StubOrderService(async cancellationToken =>
        {
            entered.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync();

                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM TableThatDoesNotExist";

                await command.ExecuteReaderAsync();
            }
        });

        using var factory = new ApiFactory(ConnectionString, orderService: orderService);

        await DisconnectDuringRequestAsync(factory, entered.Task);

        var statusCode = await factory.Outcome.StatusCode.Task.WaitAsync(TestTimeout);

        Assert.Equal(StatusCodes.Status500InternalServerError, statusCode);

        var entry = Assert.Single(
            factory.Logs.Entries,
            log => log.Category == HandlerCategory && log.Level == LogLevel.Error);

        Assert.IsType<SqlException>(entry.Exception);
    }

    // The client is still connected, so nothing explains this cancellation away.
    [SqlServerFact]
    public async Task ServerSideCancellation_WithClientConnected_ReturnsServerError()
    {
        var orderService = new StubOrderService(async _ =>
        {
            using var serverSideTimeout = new CancellationTokenSource(CancelDelay);

            await using var dbContext = CreateDbContext(new SlowQueryInterceptor());

            await new OrderService(dbContext).GetByIdAsync(1, serverSideTimeout.Token);
        });

        using var factory = new ApiFactory(ConnectionString, orderService: orderService);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/orders/1");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(
            factory.Logs.Entries,
            log => log.Category == HandlerCategory && log.Level == LogLevel.Error);
    }
}
