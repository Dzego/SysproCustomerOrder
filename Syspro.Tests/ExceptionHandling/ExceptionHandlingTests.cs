using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.Data;
using Syspro.Api.DTOs;
using Syspro.Api.ExceptionHandling;
using Syspro.Api.Services;
using Xunit;

namespace Syspro.Tests.ExceptionHandling;

// Every test goes through the real middleware pipeline, because ordering and
// the framework's own exception handling are part of what is being verified.
public sealed class ExceptionHandlingTests
{
    private const string ProblemContentType = "application/problem+json";
    private const string SensitiveText = "Server=prod-sql;Password=hunter2";
    private const string FailingEndpoint = "/api/orders/1";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private sealed class ThrowingOrderService(Func<CancellationToken, Task> behaviour)
        : IOrderService
    {
        public async Task<OrderResponse> CreateAsync(
            CreateOrderRequest request,
            CancellationToken cancellationToken)
        {
            await behaviour(cancellationToken);

            throw new InvalidOperationException("The behaviour was expected to throw.");
        }

        public async Task<OrderResponse?> GetByIdAsync(
            int id,
            CancellationToken cancellationToken)
        {
            await behaviour(cancellationToken);

            throw new InvalidOperationException("The behaviour was expected to throw.");
        }

        public async Task<List<CustomerOrderTotalResponse>> GetCustomerTotalsAsync(
            DateTime fromDate,
            DateTime toDate,
            CancellationToken cancellationToken)
        {
            await behaviour(cancellationToken);

            throw new InvalidOperationException("The behaviour was expected to throw.");
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(categoryName, Entries);
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue(new LogEntry(category, logLevel, exception));
            }
        }
    }

    // Records the outcome as seen from outside the exception handling middleware.
    private sealed class OutcomeRecorder : IStartupFilter
    {
        public TaskCompletionSource<int> StatusCode { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    try
                    {
                        await nextMiddleware(context);

                        StatusCode.TrySetResult(context.Response.StatusCode);
                    }
                    catch (Exception exception)
                    {
                        StatusCode.TrySetException(exception);

                        throw;
                    }
                });

                next(app);
            };
        }
    }

    private sealed class ApiFactory(
        string environment,
        IOrderService? orderService = null,
        string? contentRoot = null) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString();

        public CapturingLoggerProvider Logs { get; } = new();

        public OutcomeRecorder Outcome { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);

            if (contentRoot is not null)
            {
                builder.UseContentRoot(contentRoot);
            }

            builder.ConfigureLogging(logging => logging.AddProvider(Logs));

            builder.ConfigureServices(services =>
            {
                services.Configure<CustomerImportWorkerOptions>(options => options.Enabled = false);

                services.AddSingleton<IStartupFilter>(Outcome);

                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(_databaseName));

                if (orderService is not null)
                {
                    services.RemoveAll<IOrderService>();
                    services.AddSingleton(orderService);
                }
            });
        }
    }

    private static ApiFactory CreateFactoryThrowing(
        Exception exception,
        string environment = "Production")
    {
        return new ApiFactory(
            environment,
            new ThrowingOrderService(_ => Task.FromException(exception)));
    }

    private static async Task<JsonElement> ReadProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(ProblemContentType, response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((int)expectedStatus, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));

        return problem;
    }

    // ---------- Known exceptions ----------

    [Fact]
    public async Task BusinessRuleException_ReturnsBadRequestWithItsMessage()
    {
        // Arrange
        using var factory = CreateFactoryThrowing(new BusinessRuleException("Rule broken."));
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync(FailingEndpoint);

        // Assert
        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);

        Assert.Equal("Rule broken.", problem.GetProperty("detail").GetString());
        Assert.Equal(FailingEndpoint, problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task ResourceNotFoundException_ReturnsNotFoundWithItsMessage()
    {
        // Arrange
        using var factory = CreateFactoryThrowing(new ResourceNotFoundException("Gone."));
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync(FailingEndpoint);

        // Assert
        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);

        Assert.Equal("Gone.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ImportInputTooLargeException_ReturnsPayloadTooLarge()
    {
        // Arrange
        using var factory = CreateFactoryThrowing(new ImportInputTooLargeException(1024));
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync(FailingEndpoint);

        // Assert
        var problem = await ReadProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);

        Assert.Equal(
            "The legacy customer file exceeds the maximum import size of 1024 bytes.",
            problem.GetProperty("detail").GetString());
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task BadHttpRequestException_ReturnsTheStatusCodeItCarries()
    {
        // Arrange
        using var factory = CreateFactoryThrowing(new BadHttpRequestException(
            "Request body too large.",
            StatusCodes.Status413PayloadTooLarge));
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync(FailingEndpoint);

        // Assert
        await ReadProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task KnownException_WhenAcceptHeaderExcludesJson_KeepsItsStatusCode()
    {
        // Arrange
        using var factory = CreateFactoryThrowing(new ResourceNotFoundException("Gone."));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/xml"));

        // Act
        var response = await client.GetAsync(FailingEndpoint);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- Real application paths ----------

    [Fact]
    public async Task CustomerTotals_WithInvertedDateRange_ReturnsProblemDetails()
    {
        // Arrange
        using var factory = new ApiFactory("Production");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync(
            "/api/customers/totals?fromDate=2026-12-31&toDate=2026-01-01");

        // Assert
        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);

        Assert.Equal(
            "fromDate cannot be later than toDate.",
            problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task CreateOrder_WhenCustomerDoesNotExist_ReturnsProblemDetails()
    {
        // Arrange
        using var factory = new ApiFactory("Production");
        using var client = factory.CreateClient();

        var request = new
        {
            legacyCustomerId = "9999999999",
            currency = "ZAR",
            status = "Created",
            items = new[]
            {
                new { sku = "SKU-001", description = "Keyboard", unitPrice = 500m, quantity = 1 }
            }
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/orders", request);

        // Assert
        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);

        Assert.Equal("Customer was not found.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetOrderById_WhenOrderDoesNotExist_ReturnsProblemDetails()
    {
        // Arrange
        using var factory = new ApiFactory("Production");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/orders/999");

        // Assert
        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);

        Assert.Equal(
            "Order with id 999 was not found.",
            problem.GetProperty("detail").GetString());
        Assert.Equal("/api/orders/999", problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task ImportCustomers_WhenLegacyFileIsMissing_ReturnsProblemDetails()
    {
        // Arrange
        var contentRoot = Directory.CreateTempSubdirectory();

        try
        {
            using var factory = new ApiFactory("Production", contentRoot: contentRoot.FullName);
            using var client = factory.CreateClient();

            // Act
            var response = await client.PostAsync("/api/import/customers", content: null);

            // Assert
            var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);

            Assert.Equal(
                "Legacy customer file was not found.",
                problem.GetProperty("detail").GetString());
        }
        finally
        {
            contentRoot.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("/api/does-not-exist", HttpStatusCode.NotFound)]
    [InlineData("/api/customers/totals?fromDate=not-a-date&toDate=2026-01-01", HttpStatusCode.BadRequest)]
    public async Task FrameworkGeneratedErrors_ReturnProblemDetails(
        string path,
        HttpStatusCode expectedStatus)
    {
        // Arrange
        using var factory = new ApiFactory("Production");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync(path);

        // Assert
        await ReadProblemAsync(response, expectedStatus);
    }

    // ---------- Unexpected exceptions ----------

    public static TheoryData<Exception> UnexpectedExceptions => new()
    {
        new Exception(SensitiveText),
        new InvalidOperationException(SensitiveText),
        new DbUpdateException(SensitiveText, new Exception(SensitiveText)),
        // These two used to be caught as client errors. The framework and BCL
        // also throw them for server-side faults, so they must not be 4xx.
        new ArgumentException(SensitiveText),
        new ArgumentNullException("connectionString", SensitiveText),
        new KeyNotFoundException(SensitiveText),
    };

    [Theory]
    [MemberData(nameof(UnexpectedExceptions))]
    public async Task UnexpectedException_InProduction_ReturnsGenericServerError(
        Exception exception)
    {
        // Arrange
        using var factory = CreateFactoryThrowing(exception);
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync(FailingEndpoint);

        // Assert
        var body = await response.Content.ReadAsStringAsync();

        var problem = await ReadProblemAsync(response, HttpStatusCode.InternalServerError);

        Assert.Contains("traceId", problem.GetProperty("detail").GetString());
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("prod-sql", body);
        Assert.DoesNotContain(exception.GetType().Name, body);
        Assert.DoesNotContain(nameof(ThrowingOrderService), body);
        Assert.DoesNotContain(" at ", body);
    }

    [Fact]
    public async Task UnexpectedException_InDevelopment_IncludesExceptionDetails()
    {
        // Arrange
        using var factory = CreateFactoryThrowing(
            new InvalidOperationException("Developer-facing failure."),
            environment: "Development");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync(FailingEndpoint);

        // Assert
        var problem = await ReadProblemAsync(response, HttpStatusCode.InternalServerError);

        Assert.Contains("Developer-facing failure.", problem.GetProperty("detail").GetString());
    }

    // ---------- Logging ----------

    [Fact]
    public async Task UnexpectedException_IsLoggedOnceAsErrorWithTheException()
    {
        // Arrange
        var exception = new InvalidOperationException(SensitiveText);

        using var factory = CreateFactoryThrowing(exception);
        using var client = factory.CreateClient();

        // Act
        await client.GetAsync(FailingEndpoint);

        // Assert
        var entry = Assert.Single(factory.Logs.Entries, e => e.Level >= LogLevel.Error);

        Assert.Equal(typeof(GlobalExceptionHandler).FullName, entry.Category);
        Assert.Same(exception, entry.Exception);
    }

    [Fact]
    public async Task ExpectedClientError_IsNotLoggedAsAnError()
    {
        // Arrange
        using var factory = CreateFactoryThrowing(new ResourceNotFoundException("Gone."));
        using var client = factory.CreateClient();

        // Act
        await client.GetAsync(FailingEndpoint);

        // Assert
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    // ---------- FluentValidation ----------

    [Fact]
    public async Task ValidationFailure_KeepsFieldErrorsAndGainsTheSharedFields()
    {
        // Arrange
        using var factory = new ApiFactory("Production");
        using var client = factory.CreateClient();

        var request = new
        {
            customerId = 1,
            currency = "ZA",
            status = "Created",
            items = new[]
            {
                new { sku = "SKU-001", description = "Keyboard", unitPrice = 500m, quantity = 0 }
            }
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/orders", request);

        // Assert
        var body = await response.Content.ReadAsStringAsync();

        await ReadProblemAsync(response, HttpStatusCode.BadRequest);

        var problem = JsonSerializer.Deserialize<ValidationProblemDetails>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(problem);
        Assert.Equal("/api/orders", problem.Instance);
        Assert.Equal(
            new[] { "Currency", "Items[0].Quantity" },
            problem.Errors.Keys.Order());
    }

    [Fact]
    public async Task MalformedJson_ReturnsValidationProblemNotServerError()
    {
        // Arrange
        using var factory = new ApiFactory("Production");
        using var client = factory.CreateClient();

        using var content = new StringContent(
            "{ not json",
            System.Text.Encoding.UTF8,
            "application/json");

        // Act
        var response = await client.PostAsync("/api/orders", content);

        // Assert
        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);

        Assert.True(problem.TryGetProperty("errors", out _));
    }

    // ---------- Cancellation ----------

    [Fact]
    public async Task ClientDisconnect_IsNotTreatedAsAServerError()
    {
        // Arrange
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var orderService = new ThrowingOrderService(async cancellationToken =>
        {
            entered.TrySetResult();

            await Task.Delay(Timeout.Infinite, cancellationToken);
        });

        using var factory = new ApiFactory("Production", orderService);
        using var client = factory.CreateClient();
        using var cancellationSource = new CancellationTokenSource();

        // Act
        var sendTask = client.GetAsync(FailingEndpoint, cancellationSource.Token);

        await entered.Task.WaitAsync(TestTimeout);

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        // Assert
        var statusCode = await factory.Outcome.StatusCode.Task.WaitAsync(TestTimeout);

        Assert.NotEqual(StatusCodes.Status500InternalServerError, statusCode);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    private static async Task<(ApiFactory Factory, int StatusCode)> DisconnectThenThrowAsync(
        Exception exception)
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var orderService = new ThrowingOrderService(async cancellationToken =>
        {
            entered.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw exception;
            }
        });

        var factory = new ApiFactory("Production", orderService);

        using var client = factory.CreateClient();
        using var cancellationSource = new CancellationTokenSource();

        var sendTask = client.GetAsync(FailingEndpoint, cancellationSource.Token);

        await entered.Task.WaitAsync(TestTimeout);

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        var statusCode = await factory.Outcome.StatusCode.Task.WaitAsync(TestTimeout);

        return (factory, statusCode);
    }

    // EF Core wraps failures during a save, so the middleware does not recognize
    // this as a cancellation and it reaches the handler.
    [Fact]
    public async Task ClientDisconnect_WithWrappedCancellation_IsNotLoggedAsAnError()
    {
        var (factory, statusCode) = await DisconnectThenThrowAsync(
            new DbUpdateException("Save failed.", new OperationCanceledException()));

        using (factory)
        {
            Assert.Equal(StatusCodes.Status499ClientClosedRequest, statusCode);
            Assert.DoesNotContain(factory.Logs.Entries, e => e.Level >= LogLevel.Error);
            Assert.Contains(
                factory.Logs.Entries,
                e => e.Category == typeof(GlobalExceptionHandler).FullName && e.Level == LogLevel.Information);
        }
    }

    // A real fault must stay visible even though the client has also gone.
    [Fact]
    public async Task ClientDisconnect_WithUnrelatedFailure_IsStillLoggedAsAnError()
    {
        var failure = new InvalidOperationException(SensitiveText);

        var (factory, statusCode) = await DisconnectThenThrowAsync(failure);

        using (factory)
        {
            Assert.Equal(StatusCodes.Status500InternalServerError, statusCode);

            var entry = Assert.Single(factory.Logs.Entries, e => e.Level >= LogLevel.Error);

            Assert.Equal(typeof(GlobalExceptionHandler).FullName, entry.Category);
            Assert.Same(failure, entry.Exception);
        }
    }

    [Fact]
    public async Task WrappedCancellation_WithoutClientDisconnect_ReturnsServerError()
    {
        using var factory = CreateFactoryThrowing(
            new DbUpdateException("Save failed.", new OperationCanceledException()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(FailingEndpoint);

        await ReadProblemAsync(response, HttpStatusCode.InternalServerError);
        Assert.Single(factory.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    public static TheoryData<Exception, bool> ExceptionsToClassify => new()
    {
        { new OperationCanceledException(), true },
        { new TaskCanceledException(), true },
        { new DbUpdateException("Save failed.", new TaskCanceledException()), true },
        { new InvalidOperationException(), false },
        { new DbUpdateException("Save failed.", new InvalidOperationException()), false },
        { new TimeoutException(), false }
    };

    [Theory]
    [MemberData(nameof(ExceptionsToClassify))]
    public void IsCancellation_RecognizesOnlyCancellationExceptions(Exception exception, bool expected)
    {
        Assert.Equal(expected, CancellationExceptionClassifier.IsCancellation(exception));
    }

    // Nothing disconnected, so this is a server-side timeout that must be visible.
    [Fact]
    public async Task OperationCanceled_WithoutClientDisconnect_ReturnsServerError()
    {
        // Arrange
        using var factory = CreateFactoryThrowing(new OperationCanceledException());
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync(FailingEndpoint);

        // Assert
        await ReadProblemAsync(response, HttpStatusCode.InternalServerError);
        Assert.Single(factory.Logs.Entries, e => e.Level >= LogLevel.Error);
    }
}
