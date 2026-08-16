using System.Globalization;
using Syspro.Api.DTOs;

namespace Syspro.Api.Services;

public class LegacyCustomerParser
{
    public LegacyCustomerRecord Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new ArgumentException("Line cannot be empty.");
        }

        if (line.Length < 80)
        {
            throw new FormatException(
                $"Invalid line length. Expected at least 80 characters but got {line.Length}.");
        }

        var legacyCustomerId = line.Substring(0, 10).Trim();
        var name = line.Substring(10, 30).Trim();
        var email = line.Substring(40, 30).Trim();
        var signupDateText = line.Substring(70, 8).Trim();
        var tier = line.Substring(78, 2).Trim();

        if (!DateOnly.TryParseExact(
                signupDateText,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var signupDate))
        {
            throw new FormatException($"Invalid signup date: {signupDateText}");
        }

        if (tier is not ("A" or "B" or "C"))
        {
            throw new FormatException($"Invalid tier: {tier}");
        }

        return new LegacyCustomerRecord
        {
            LegacyCustomerId = legacyCustomerId,
            Name = name,
            Email = email,
            SignupDate = signupDate,
            Tier = tier
        };
    }
}