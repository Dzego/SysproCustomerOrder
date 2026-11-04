namespace Syspro.Api.BackgroundJobs;

public sealed class CustomerImportWorkerOptions
{
    public const string SectionName = "CustomerImportWorker";

    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(5);

    // Instances with this off still accept jobs and report status, but never process them.
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxAttempts { get; set; } = 3;

    public bool HasValidPollInterval =>
        PollInterval > TimeSpan.Zero && PollInterval <= MaxPollInterval;
}
