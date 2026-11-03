namespace Syspro.Api.Services;

// A resource named by the request does not exist. The message is returned to
// the client, so it must be safe to expose.
public sealed class ResourceNotFoundException : Exception
{
    public ResourceNotFoundException(string message)
        : base(message)
    {
    }
}
