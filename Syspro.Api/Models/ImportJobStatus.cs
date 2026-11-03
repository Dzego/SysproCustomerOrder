namespace Syspro.Api.Models;

// Pending -> Processing -> Completed or Failed
// Processing -> Pending, when an interrupted job is queued to run again
public enum ImportJobStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}
