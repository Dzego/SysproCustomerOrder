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

    private static string BuildLine(
        string legacyCustomerId = "0000012345",
        string name = "John Smith",
        string email = "john.smith@example.com",
        string signupDate = "20210315",
        string tier = "A")
    {
        return legacyCustomerId.PadRight(10) +
               name.PadRight(30) +
               email.PadRight(30) +
               signupDate.PadRight(8) +
               tier.PadRight(2);
    }

    // ---------- Line length ----------

    [Theory]
    [InlineData(79)]
    [InlineData(81)]
    public void Parse_LineOneCharacterOffExpectedLength_ReportsActualLength(int length)
    {
        var line = BuildLine().PadRight(81)[..length];

        var exception = Assert.Throws<LegacyCustomerParseException>(() => _parser.Parse(line));

        Assert.Equal(
            $"Invalid line length. Expected 80 characters but got {length}.",
            exception.Message);
    }

    [Fact]
    public void Parse_WhitespaceOnlyLine_IsTreatedAsEmpty()
    {
        var exception = Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(new string(' ', 80)));

        Assert.Equal("Line cannot be empty.", exception.Message);
    }

    // ---------- Field extraction ----------

    [Fact]
    public void Parse_FieldsFillingTheirWholeWidth_DoNotBleedIntoEachOther()
    {
        var name = new string('N', 30);
        var email = new string('E', 30);

        var result = _parser.Parse(BuildLine("1234567890", name, email, "20210315", "C"));

        Assert.Equal("1234567890", result.LegacyCustomerId);
        Assert.Equal(name, result.Name);
        Assert.Equal(email, result.Email);
        Assert.Equal(new DateOnly(2021, 3, 15), result.SignupDate);
        Assert.Equal("C", result.Tier);
    }

    [Fact]
    public void Parse_TrimsSurroundingSpacesButKeepsInnerSpaces()
    {
        var result = _parser.Parse(BuildLine(legacyCustomerId: "  123", name: "  Mary  Ann Lee"));

        Assert.Equal("123", result.LegacyCustomerId);
        Assert.Equal("Mary  Ann Lee", result.Name);
    }

    // ---------- Signup date ----------

    [Fact]
    public void Parse_LeapDayInLeapYear_IsValid()
    {
        var result = _parser.Parse(BuildLine(signupDate: "20240229"));

        Assert.Equal(new DateOnly(2024, 2, 29), result.SignupDate);
    }

    [Theory]
    [InlineData("20230229")]
    [InlineData("20211301")]
    [InlineData("20210100")]
    [InlineData("00000000")]
    [InlineData("2021-3-1")]
    [InlineData("15032021")]
    [InlineData("")]
    public void Parse_UnusableSignupDate_ReportsTheDate(string signupDate)
    {
        var exception = Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(BuildLine(signupDate: signupDate)));

        Assert.Equal($"Invalid signup date: {signupDate}", exception.Message);
    }

    // ---------- Tier ----------

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("C")]
    public void Parse_SupportedTier_IsValid(string tier)
    {
        Assert.Equal(tier, _parser.Parse(BuildLine(tier: tier)).Tier);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("D")]
    [InlineData("AB")]
    [InlineData("")]
    public void Parse_UnsupportedTier_ReportsTheTier(string tier)
    {
        var exception = Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(BuildLine(tier: tier)));

        Assert.Equal($"Invalid tier: {tier}", exception.Message);
    }

    // ---------- Legacy customer ID ----------

    [Theory]
    [InlineData("1")]
    [InlineData("123")]
    [InlineData("ABC123")]
    [InlineData("1234567890")]
    [InlineData("A-1_b/2!?~")]
    [InlineData("AB CD")]
    public void Parse_IdOfOneToTenPrintableAsciiCharacters_IsAccepted(string legacyCustomerId)
    {
        Assert.Equal(legacyCustomerId, _parser.Parse(BuildLine(legacyCustomerId)).LegacyCustomerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("          ")]
    [InlineData("\t")]
    [InlineData("\u3000\u3000")]
    public void Parse_BlankId_IsRejected(string legacyCustomerId)
    {
        var exception = Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(BuildLine(legacyCustomerId)));

        Assert.Equal("Invalid LegacyCustomerId: the value is blank.", exception.Message);
    }

    // Each of these becomes '?' in a varchar column, or could not be matched reliably, so
    // accepting them could update an unrelated customer.
    [Theory]
    [InlineData("日本")]
    [InlineData("12é45")]
    [InlineData("A\tB")]
    [InlineData("A\u0000B")]
    [InlineData("A\u007FB")]
    [InlineData("\uFFFD123")]
    [InlineData("😀")]
    public void Parse_IdWithUnsupportedCharacters_IsRejected(string legacyCustomerId)
    {
        var exception = Assert.Throws<LegacyCustomerParseException>(
            () => _parser.Parse(BuildLine(legacyCustomerId)));

        Assert.Equal(
            "Invalid LegacyCustomerId: only printable ASCII characters are allowed.",
            exception.Message);
    }

    // The ID rule changed these, and nothing else: other fields stay as permissive as before.
    [Theory]
    [InlineData("", "john@example.com")]
    [InlineData("John Smith", "")]
    public void Parse_BlankNameOrEmail_IsStillAccepted(string name, string email)
    {
        var result = _parser.Parse(BuildLine("0000012345", name, email));

        Assert.Equal(name, result.Name);
        Assert.Equal(email, result.Email);
    }
}
