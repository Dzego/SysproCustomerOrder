namespace Syspro.Api.Services;

// The request broke a business rule. The message is returned to the client, so
// it must be safe to expose.
public sealed class BusinessRuleException : Exception
{
    public BusinessRuleException(string message)
        : base(message)
    {
    }
}
