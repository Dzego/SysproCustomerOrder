using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Syspro.Api.BackgroundJobs;
using Syspro.Api.Services;
using Syspro.Tests.TestSupport;
using Xunit;

namespace Syspro.Tests.BackgroundJobs;

// Uses a very long poll interval wherever the worker should be waiting, so a worker
// that failed to wait would be caught by the call counts, with no timing involved.
public sealed class CustomerImportWorkerTests : IAsyncDisposable
{
    private static readonly TimeSpan NeverPolls = TimeSpan.FromHours(1);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly ScriptedRunner _runner = new();
    private readonly CapturingLogger<CustomerImportWorker> _logger = new();
    private ServiceProvider? _serviceProvider;

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
    }

    private sealed class ScriptedRunner : ICustomerImportJobRunner
    {
        private readonly Queue<Func<CancellationToken, Task<bool>>> _runs = new();

        public int RunCount { get; private set; }

        // Fixed when the script ends, because a worker with a short poll interval
        // can keep calling until the test stops it.
        public int RunCountAtIdle { get; private set; }

        public TaskCompletionSource Idle { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ThenRun(Func<CancellationToken, Task<bool>> run)
        {
            _runs.Enqueue(run);
        }

        public void ThenRun(params bool[] results)
        {
            foreach (var result in results)
            {
                _runs.Enqueue(_ => Task.FromResult(result));
            }
        }

        public Task<bool> RunNextJobAsync(CancellationToken stoppingToken)
        {
            RunCount++;

            if (_runs.TryDequeue(out var run))
            {
                return run(stoppingToken);
            }

            // The script has ended, so the worker is about to wait for its next poll.
            if (!Idle.Task.IsCompleted)
            {
                RunCountAtIdle = RunCount;
                Idle.TrySetResult();
            }

            return Task.FromResult(false);
        }
    }

    private CustomerImportWorker CreateWorker(
        TimeSpan pollInterval,
        bool enabled = true,
        Action? onScopeCreated = null)
    {
        var services = new ServiceCollection();

        services.AddScoped<ICustomerImportJobRunner>(_ =>
        {
            onScopeCreated?.Invoke();

            return _runner;
        });

        _serviceProvider = services.BuildServiceProvider(validateScopes: true);

        return new CustomerImportWorker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CustomerImportWorkerOptions
            {
                Enabled = enabled,
                PollInterval = pollInterval
            }),
            _logger);
    }

    private async Task RunUntilIdleAsync(CustomerImportWorker worker)
    {
        await worker.StartAsync(CancellationToken.None);

        await _runner.Idle.Task.WaitAsync(TestTimeout);

        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    // The fourth call finds nothing, and the worker then waits rather than spinning.
    [Fact]
    public async Task Worker_RunsQueuedJobsWithoutDelayThenWaitsWithoutBusyLooping()
    {
        _runner.ThenRun(true, true, true);

        await RunUntilIdleAsync(CreateWorker(NeverPolls));

        Assert.Equal(4, _runner.RunCount);
    }

    [Fact]
    public async Task Worker_PollsAgainAfterTheIntervalWhenIdle()
    {
        _runner.ThenRun(false, false);

        await RunUntilIdleAsync(CreateWorker(TimeSpan.FromMilliseconds(1)));

        Assert.Equal(3, _runner.RunCountAtIdle);
    }

    [Fact]
    public async Task Worker_UsesANewScopeForEachCycle()
    {
        var scopes = 0;

        _runner.ThenRun(true, true);

        await RunUntilIdleAsync(CreateWorker(NeverPolls, onScopeCreated: () => scopes++));

        Assert.Equal(3, scopes);
    }

    [Fact]
    public async Task Worker_WhenCycleFails_SurvivesLogsErrorAndPollsAgain()
    {
        var failure = new InvalidOperationException("Database unavailable.");

        _runner.ThenRun(_ => Task.FromException<bool>(failure));

        await RunUntilIdleAsync(CreateWorker(TimeSpan.FromMilliseconds(1)));

        Assert.Equal(2, _runner.RunCountAtIdle);
        Assert.Same(failure, Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error).Exception);
    }

    // The database may not be reachable yet when the application starts.
    [Fact]
    public async Task Worker_WhenTheDatabaseIsUnavailableAtStartup_KeepsPolling()
    {
        var failure = new InvalidOperationException("Database unavailable.");

        _runner.ThenRun(_ => Task.FromException<bool>(failure));
        _runner.ThenRun(_ => Task.FromException<bool>(failure));

        await RunUntilIdleAsync(CreateWorker(TimeSpan.FromMilliseconds(1)));

        Assert.Equal(3, _runner.RunCountAtIdle);
    }

    [Fact]
    public async Task Worker_StopsPromptlyWhileWaitingForNextPoll()
    {
        await RunUntilIdleAsync(CreateWorker(NeverPolls));

        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Worker_OnShutdownCancelsRunningJobAndStopsWithoutError()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _runner.ThenRun(async stoppingToken =>
        {
            running.TrySetResult();

            await Task.Delay(Timeout.Infinite, stoppingToken);

            return true;
        });

        var worker = CreateWorker(NeverPolls);

        await worker.StartAsync(CancellationToken.None);
        await running.Task.WaitAsync(TestTimeout);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(1, _runner.RunCount);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Worker_WhenDisabled_NeverTouchesJobs()
    {
        var worker = CreateWorker(NeverPolls, enabled: false);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(0, _runner.RunCount);
    }

    [Theory]
    [InlineData("00:00:00", false)]
    [InlineData("-00:00:01", false)]
    [InlineData("00:00:00.001", true)]
    [InlineData("01:00:00", true)]
    [InlineData("01:00:01", false)]
    public void LeaseDuration_IsBounded(string leaseDuration, bool expected)
    {
        var options = new CustomerImportOptions { LeaseDuration = TimeSpan.Parse(leaseDuration) };

        Assert.Equal(expected, options.HasValidLeaseDuration);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(CustomerImportOptions.MaxAllowedBatchSize, true)]
    [InlineData(CustomerImportOptions.MaxAllowedBatchSize + 1, false)]
    public void BatchSize_IsBounded(int batchSize, bool expected)
    {
        var options = new CustomerImportOptions { BatchSize = batchSize };

        Assert.Equal(expected, options.HasValidBatchSize);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(CustomerImportOptions.MaxAllowedInputBytes, true)]
    [InlineData(CustomerImportOptions.MaxAllowedInputBytes + 1, false)]
    public void MaxInputBytes_IsBounded(int maxInputBytes, bool expected)
    {
        var options = new CustomerImportOptions { MaxInputBytes = maxInputBytes };

        Assert.Equal(expected, options.HasValidMaxInputBytes);
    }

    [Theory]
    [InlineData("00:00:00", false)]
    [InlineData("-00:00:01", false)]
    [InlineData("00:00:00.001", true)]
    [InlineData("00:05:00", true)]
    [InlineData("00:05:01", false)]
    public void PollInterval_IsBounded(string pollInterval, bool expected)
    {
        var options = new CustomerImportWorkerOptions { PollInterval = TimeSpan.Parse(pollInterval) };

        Assert.Equal(expected, options.HasValidPollInterval);
    }
}
