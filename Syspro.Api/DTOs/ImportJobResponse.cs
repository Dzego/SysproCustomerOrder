using Syspro.Api.Models;

namespace Syspro.Api.DTOs;

// The counts show what has been committed so far, so they grow while a job runs, and a
// Failed job can have non-zero counts for the batches it applied.
public sealed class ImportJobResponse
{
    public int Id { get; init; }

    public required string Status { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? StartedAt { get; init; }

    public DateTime? CompletedAt { get; init; }

    public int Processed { get; init; }

    public int Created { get; init; }

    public int Updated { get; init; }

    public int Failed { get; init; }

    public string? FailureReason { get; init; }

    public static ImportJobResponse FromJob(ImportLog job)
    {
        return new ImportJobResponse
        {
            Id = job.Id,
            Status = job.Status.ToString(),
            CreatedAt = AsUtc(job.CreatedAt),
            StartedAt = job.StartedAt is { } startedAt ? AsUtc(startedAt) : null,
            CompletedAt = job.CompletedAt is { } completedAt ? AsUtc(completedAt) : null,
            Processed = job.ProcessedCount,
            Created = job.CreatedCount,
            Updated = job.UpdatedCount,
            Failed = job.FailedCount,
            FailureReason = job.FailureReason
        };
    }

    // datetime2 does not store a kind, and these values are always written as UTC.
    private static DateTime AsUtc(DateTime value)
    {
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
