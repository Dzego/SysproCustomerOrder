namespace Syspro.Api.Services;

// The job's persisted state is not what this execution expected, so it no longer
// owns the job and must stop without changing anything.
public sealed class ImportJobStateChangedException : Exception
{
    public ImportJobStateChangedException(int jobId)
        : base($"Import job {jobId} was changed by another execution.")
    {
    }
}
