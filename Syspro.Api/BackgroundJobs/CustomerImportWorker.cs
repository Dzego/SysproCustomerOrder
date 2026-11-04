using Microsoft.Extensions.Options;
using Syspro.Api.ExceptionHandling;
using Syspro.Api.Services;

namespace Syspro.Api.BackgroundJobs;

// Finds work by polling SQL Server, so jobs survive restarts. Any number of instances can
// run this: one holds a lease on the active job, and the others wait. A crashed worker's
// job is picked up once its lease expires, so there is no recovery step at startup.
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

        while (!stoppingToken.IsCancellationRequested)
        {
            var ranJob = false;

            try
            {
                // A new scope per job gives each one a fresh DbContext.
                await using var scope = _scopeFactory.CreateAsyncScope();

                var runner = scope.ServiceProvider.GetRequiredService<ICustomerImportJobRunner>();

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
                // Letting this escape would stop the whole host. A job left Processing, for
                // example if the database went down, is picked up when its lease expires.
                _logger.LogError(
                    exception,
                    "Customer import worker cycle failed. Retrying in {PollInterval}",
                    _options.PollInterval);
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
