namespace Syspro.Api.Services;

// The message is returned to the client, so it must be safe to expose.
public sealed class ImportInputTooLargeException : Exception
{
    public ImportInputTooLargeException(int maxBytes)
        : base($"The legacy customer file exceeds the maximum import size of {maxBytes} bytes.")
    {
    }
}
