using Microsoft.Extensions.Options;
using Syspro.Api.ExceptionHandling;
using Syspro.Api.Services;

namespace Syspro.Api.BackgroundJobs;

// Finds work by polling SQL Server, so jobs survive restarts. Runs one job at a
// time, and supports only one enabled instance: recovery treats every Processing
// job as abandoned, which would be wrong if another worker were running it.
public sealed class CustomerImportWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CustomerImportWorkerOptions _options;
    private readonly ILogger<CustomerImportWorker> _logger;

    public CustomerImportWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<CustomerImportWorkerOptions> options,
        ILogger<CustomerImportWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Customer import worker is disabled. Accepted jobs will stay Pending");

            return;
        }

        // Returns control to the host so that this loop does not delay startup.
        await Task.Yield();

        _logger.LogInformation(
            "Customer import worker started, polling every {PollInterval}",
            _options.PollInterval);

        var recoveryRequired = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            var ranJob = false;

            try
            {
                // A new scope per job gives each one a fresh DbContext.
                await using var scope = _scopeFactory.CreateAsyncScope();

                var runner = scope.ServiceProvider.GetRequiredService<ICustomerImportJobRunner>();

                if (recoveryRequired)
                {
                    await runner.RecoverInterruptedJobsAsync(stoppingToken);

                    recoveryRequired = false;
                }

                ranJob = await runner.RunNextJobAsync(stoppingToken);
            }
            catch (Exception exception) when (
                stoppingToken.IsCancellationRequested &&
                CancellationExceptionClassifier.IsCancellation(exception))
            {
                break;
            }
            catch (Exception exception)
            {
                // Letting this escape would stop the whole host. A job may have been
                // left Processing, for example if the database went down, and this
                // worker is now idle, so recovering it next time is safe.
                _logger.LogError(
                    exception,
                    "Customer import worker cycle failed. Retrying in {PollInterval}",
                    _options.PollInterval);

                recoveryRequired = true;
            }

            // Runs the next job straight away while work is queued.
            if (!ranJob && !await WaitForNextPollAsync(stoppingToken))
            {
                break;
            }
        }

        _logger.LogInformation("Customer import worker stopped");
    }

    private async Task<bool> WaitForNextPollAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_options.PollInterval, stoppingToken);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
