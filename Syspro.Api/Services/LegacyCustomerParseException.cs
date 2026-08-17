namespace Syspro.Api.Services;

public class LegacyCustomerParseException : Exception
{
    public LegacyCustomerParseException(string message)
        : base(message)
    {
    }
}