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

    // Lifetime executions of this job, for reporting.
    public int AttemptCount { get; set; }

    // Executions since the job last committed a batch, which the retry limit applies to.
    // Counted when a job is claimed, because a crash cannot record anything afterwards.
    public int ConsecutiveFailureCount { get; set; }

    // Returned to clients, so it must never contain exception details.
    public string? FailureReason { get; set; }

    public int ProcessedCount { get; set; }

    public int CreatedCount { get; set; }

    public int UpdatedCount { get; set; }

    public int FailedCount { get; set; }

    public ICollection<ImportError> Errors { get; set; } = [];

    public ImportJobInput? Input { get; set; }
}
