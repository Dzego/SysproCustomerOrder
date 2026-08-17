namespace Syspro.Api.DTOs;

public sealed class ImportResult
{
    public int Processed { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Failed { get; init; }
}