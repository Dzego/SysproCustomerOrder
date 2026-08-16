namespace Syspro.Api.Models;

public class ImportError
{
    public int Id { get; set; }

    public int ImportLogId { get; set; }

    public int LineNumber { get; set; }

    public required string RawData { get; set; }

    public required string Reason { get; set; }

    public ImportLog ImportLog { get; set; } = null!;
}