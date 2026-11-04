namespace Syspro.Api.Models;

// One row for each import execution, which doubles as its durable background job.
public class ImportLog
{
    public const int FailureReasonMaxLength = 500;

    public int Id { get; set; }

    public ImportJobStatus Status { get; set; } = ImportJobStatus.Pending;

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int AttemptCount { get; set; }

    // Returned to clients, so it must never contain exception details.
    public string? FailureReason { get; set; }

    public int ProcessedCount { get; set; }

    public int CreatedCount { get; set; }

    public int UpdatedCount { get; set; }

    public int FailedCount { get; set; }

    public ICollection<ImportError> Errors { get; set; } = [];

    public ImportJobInput? Input { get; set; }
}
