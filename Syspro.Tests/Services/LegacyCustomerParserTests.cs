using Syspro.Api.Services;
using Xunit;

namespace Syspro.Tests.Services;

public class LegacyCustomerParserTests
{
    private readonly LegacyCustomerParser _parser = new();

    [Fact]
    public void Parse_ValidLegacyLine_ReturnsExpectedCustomerRecord()
    {
        // Arrange
        var line =
            "0000012345" +
            "John Smith".PadRight(30) +
            "john.smith@example.com".PadRight(30) +
            "20210315" +
            "A ";

        // Act
        var result = _parser.Parse(line);

        // Assert
        Assert.Equal("0000012345", result.LegacyCustomerId);
        Assert.Equal("John Smith", result.Name);
        Assert.Equal("john.smith@example.com", result.Email);
        Assert.Equal(new DateOnly(2021, 3, 15), result.SignupDate);
        Assert.Equal("A", result.Tier);
    }

    [Fact]
    public void Parse_InvalidSignupDate_ThrowsLegacyCustomerParseException()
    {
        // Arrange
        var line =
            "0000012350" +
            "Invalid Date".PadRight(30) +
            "invalid.date@example.com".PadRight(30) +
            "20241340" +
            "A ";

        // Act
        var exception = Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(line));

        // Assert
        Assert.Contains("Invalid signup date", exception.Message);
    }

    [Fact]
    public void Parse_InvalidTier_ThrowsLegacyCustomerParseException()
    {
        // Arrange
        var line =
            "0000012351" +
            "Invalid Tier".PadRight(30) +
            "invalid.tier@example.com".PadRight(30) +
            "20220615" +
            "Z ";

        // Act
        var exception = Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(line));

        // Assert
        Assert.Contains("Invalid tier", exception.Message);
    }

    [Fact]
    public void Parse_LineShorterThanExpected_ThrowsLegacyCustomerParseException()
    {
        // Arrange
        var line = "0000012345John Smith";

        // Act
        var exception = Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(line));

        // Assert
        Assert.Contains("Invalid line length", exception.Message);
    }

    [Fact]
    public void Parse_EmptyLine_ThrowsLegacyCustomerParseException()
    {
        // Arrange
        var line = string.Empty;

        // Act
        var exception = Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(line));

        // Assert
        Assert.Contains("Line cannot be empty", exception.Message);
    }
}