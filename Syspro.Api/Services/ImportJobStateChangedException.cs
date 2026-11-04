namespace Syspro.Api.Services;

// This worker's fencing token or checkpoint no longer matches the job, because another
// worker has reclaimed it. This worker must stop without changing anything.
public sealed class ImportJobStateChangedException : Exception
{
    public ImportJobStateChangedException(int jobId)
        : base($"Import job {jobId} is no longer owned by this worker.")
    {
    }
}
