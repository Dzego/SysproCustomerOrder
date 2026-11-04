namespace Syspro.Api.Services;

public sealed class CustomerImportOptions
{
    public const string SectionName = "CustomerImport";

    public const int MaxAllowedInputBytes = 1024 * 1024 * 1024;

    public const int MaxAllowedBatchSize = 10_000;

    // The whole file is held in memory while a job is accepted, and stored as one value.
    public int MaxInputBytes { get; set; } = 50 * 1024 * 1024;

    // Lines committed in each transaction. Larger batches hold locks for longer.
    public int BatchSize { get; set; } = 1_000;

    public bool HasValidMaxInputBytes =>
        MaxInputBytes is > 0 and <= MaxAllowedInputBytes;

    public bool HasValidBatchSize =>
        BatchSize is > 0 and <= MaxAllowedBatchSize;
}
