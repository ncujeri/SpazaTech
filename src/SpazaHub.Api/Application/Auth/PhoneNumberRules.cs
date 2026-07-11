using System.Text.RegularExpressions;

namespace SpazaHub.Api.Auth;

/// <summary>
/// South African mobile number handling. Accepts local format (0821234567) or
/// E.164 (+27821234567) and normalizes to E.164.
/// </summary>
public static partial class PhoneNumberRules
{
    [GeneratedRegex(@"^\+27[1-9]\d{8}$")]
    private static partial Regex E164ZaRegex();

    [GeneratedRegex(@"^0[1-9]\d{8}$")]
    private static partial Regex LocalZaRegex();

    /// <summary>Tries to normalize input to E.164 (+27...). Strips spaces and dashes.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string cleaned = input.Replace(" ", string.Empty).Replace("-", string.Empty);

        if (E164ZaRegex().IsMatch(cleaned))
        {
            normalized = cleaned;
            return true;
        }

        if (LocalZaRegex().IsMatch(cleaned))
        {
            normalized = string.Concat("+27", cleaned.AsSpan(1));
            return true;
        }

        return false;
    }

    public static bool IsValid(string? input) => TryNormalize(input, out _);
}
