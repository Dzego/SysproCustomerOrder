using Syspro.Api.Services;
using Xunit;

namespace Syspro.Tests.Services;
public class LegacyCustomerParserTests
{
    private readonly LegacyCustomerParser _parser = new();

    [Fact]
    public void Parse_ValidLine_ReturnsExpectedCustomer()
    {
        var line =
            "0000012345" +
            "John Smith".PadRight(30) +
            "john.smith@example.com".PadRight(30) +
            "20210315" +
            "A ";

        var result = _parser.Parse(line);

        Assert.Equal("0000012345", result.LegacyCustomerId);
        Assert.Equal("John Smith", result.Name);
        Assert.Equal("john.smith@example.com", result.Email);
        Assert.Equal(new DateOnly(2021, 3, 15), result.SignupDate);
        Assert.Equal("A", result.Tier);
    }

    [Fact]
    public void Parse_InvalidDate_ThrowsLegacyCustomerParseException()
    {
        var line =
            "0000012345" +
            "John Smith".PadRight(30) +
            "john.smith@example.com".PadRight(30) +
            "20211340" +
            "A ";

        Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(line));
    }
}