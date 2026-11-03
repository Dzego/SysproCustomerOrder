using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Syspro.Tests.TestSupport;

public sealed record LoggedEntry(LogLevel Level, Exception? Exception);

public sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<LoggedEntry> Entries { get; } = new();

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
        Entries.Enqueue(new LoggedEntry(logLevel, exception));
    }
}

public sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";

    public string ApplicationName { get; set; } = "Syspro.Tests";

    public string ContentRootPath { get; set; } = "/content-root";

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
