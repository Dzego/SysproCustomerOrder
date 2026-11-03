using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.Data;
using Syspro.Api.Services;

namespace Syspro.IntegrationTests.Infrastructure;

// Runs the real API against the test database. The "Testing" environment means
// user secrets, which only load in Development, are never read.
public sealed class ApiFactory(
    string connectionString,
    string? contentRoot = null,
    IInterceptor[]? interceptors = null,
    IOrderService? orderService = null,
    bool runImportWorker = false) : WebApplicationFactory<Program>
{
    public CapturedLogs Logs { get; } = new();

    public RequestOutcome Outcome { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        if (contentRoot is not null)
        {
            builder.UseContentRoot(contentRoot);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(Outcome);

            services.Configure<CustomerImportWorkerOptions>(options =>
            {
                options.Enabled = runImportWorker;
                options.PollInterval = TimeSpan.FromMilliseconds(50);
            });

            services.RemoveAll<DbContextOptions<AppDbContext>>();

            services.AddDbContext<AppDbContext>(options =>
                options
                    .UseSqlServer(connectionString)
                    .AddInterceptors(interceptors ?? []));

            if (orderService is not null)
            {
                services.RemoveAll<IOrderService>();
                services.AddSingleton(orderService);
            }
        });
    }
}

public sealed record CapturedLog(string Category, LogLevel Level, Exception? Exception);

public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(categoryName, Entries);
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        string category,
        ConcurrentQueue<CapturedLog> entries) : ILogger
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
            entries.Enqueue(new CapturedLog(category, logLevel, exception));
        }
    }
}

// Records the final status from outside the exception handling middleware. A
// disconnected client cannot see it, so this is the only way to observe it.
public sealed class RequestOutcome : IStartupFilter
{
    public TaskCompletionSource<int> StatusCode { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                await nextMiddleware(context);

                StatusCode.TrySetResult(context.Response.StatusCode);
            });

            next(app);
        };
    }
}
