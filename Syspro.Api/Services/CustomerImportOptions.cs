namespace Syspro.Api.Services;

public sealed class CustomerImportOptions
{
    public const string SectionName = "CustomerImport";

    public const int MaxAllowedInputBytes = 1024 * 1024 * 1024;

    // The whole file is held in memory and stored as a single value.
    public int MaxInputBytes { get; set; } = 50 * 1024 * 1024;

    public bool HasValidMaxInputBytes =>
        MaxInputBytes is > 0 and <= MaxAllowedInputBytes;
}
