namespace Syspro.Api.Models;

// The exact input an accepted job processes, so that it no longer depends on the
// file. A separate table keeps job polling and status reads from loading it.
// Kept while the job is Pending or Processing, and deleted once it has finished.
public class ImportJobInput
{
    public int ImportLogId { get; set; }

    public required byte[] Content { get; set; }

    public ImportLog ImportLog { get; set; } = null!;
}
