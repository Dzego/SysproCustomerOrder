using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Syspro.IntegrationTests.Infrastructure;

// Sends warnings and errors to the test's output, so that the cause of a retry or an
// unexpected failure can be read from the results instead of being lost.
public sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= LogLevel.Warning;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            output.WriteLine($"{logLevel}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}");
        }
    }
}
