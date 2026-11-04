namespace Syspro.Api.BackgroundJobs;

public sealed class CustomerImportWorkerOptions
{
    public const string SectionName = "CustomerImportWorker";

    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(5);

    // Must be true on only one running instance, including while deployments overlap.
    // Nothing enforces this. See "Deployment Restriction" in the README.
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxAttempts { get; set; } = 3;

    public bool HasValidPollInterval =>
        PollInterval > TimeSpan.Zero && PollInterval <= MaxPollInterval;
}
