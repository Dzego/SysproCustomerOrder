namespace Syspro.Api.Services;

public sealed class CustomerImportOptions
{
    public const string SectionName = "CustomerImport";

    public const int MaxAllowedInputBytes = 1024 * 1024 * 1024;

    public const int MaxAllowedBatchSize = 10_000;

    public static readonly TimeSpan MaxAllowedLeaseDuration = TimeSpan.FromHours(1);

    // The whole file is held in memory while a job is accepted, and stored as one value.
    public int MaxInputBytes { get; set; } = 50 * 1024 * 1024;

    // Lines committed in each transaction. Larger batches hold locks for longer.
    public int BatchSize { get; set; } = 1_000;

    // How long a job stays claimed without a batch renewing its lease. This is how long a
    // crashed worker's job waits before another worker takes it over. It should be well above
    // the time a batch takes, or healthy batches will be fenced and their work repeated.
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    public bool HasValidLeaseDuration =>
        LeaseDuration > TimeSpan.Zero && LeaseDuration <= MaxAllowedLeaseDuration;

    public bool HasValidMaxInputBytes =>
        MaxInputBytes is > 0 and <= MaxAllowedInputBytes;

    public bool HasValidBatchSize =>
        BatchSize is > 0 and <= MaxAllowedBatchSize;
}
