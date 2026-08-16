namespace Syspro.Api.Models;

public class ImportLog
{
    public int Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int ProcessedCount { get; set; }

    public int CreatedCount { get; set; }

    public int UpdatedCount { get; set; }

    public int FailedCount { get; set; }

    public ICollection<ImportError> Errors { get; set; } = [];
}