using System.Text;

namespace SpazaHub.Domain.Services;

/// <summary>
/// Tier 1 payment reminders: an sms: deep link that opens the owner's own SMS app with
/// the message prefilled. Zero cost, zero aggregator dependency, works on any phone.
/// Messages are built to fit one 160-character GSM-7 segment and avoid characters that
/// flip the encoding to UCS-2 (which would triple the owner's cost on tier 2 later).
/// </summary>
public static class ReminderSms
{
    public const int MaxGsmSegmentLength = 160;

    /// <summary>Characters safe in the basic GSM-7 alphabet that we allow through.</summary>
    private const string Gsm7Safe =
        "@£$¥èéùìòÇØøÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑܧ¿abcdefghijklmnopqrstuvwxyzäöñüà\n\r";

    /// <summary>
    /// Builds a friendly reminder body in English or isiZulu that always fits one
    /// GSM-7 segment. Long shop or customer names are trimmed rather than pushed
    /// into a second segment.
    /// </summary>
    public static string BuildBody(string customerName, decimal balance, string shopName, string language = "en")
    {
        string name = Sanitize(customerName, 24);
        string shop = Sanitize(shopName, 30);
        string amount = balance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        string body = language.StartsWith("zu", StringComparison.OrdinalIgnoreCase)
            ? $"Sawubona {name}, sikhumbuza ngesikweletu sika R{amount} e-{shop}. Siyabonga!"
            : $"Hello {name}, friendly reminder of R{amount} owing at {shop}. Thank you!";

        return body.Length <= MaxGsmSegmentLength ? body : body[..MaxGsmSegmentLength];
    }

    /// <summary>sms: deep link with the prefilled body. Opens the phone's SMS app.</summary>
    public static string BuildLink(string phoneE164, string body)
        => $"sms:{phoneE164}?body={Uri.EscapeDataString(body)}";

    /// <summary>Keeps only GSM-7 safe characters and trims to a display length.</summary>
    private static string Sanitize(string input, int maxLength)
    {
        var builder = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (Gsm7Safe.Contains(c))
            {
                builder.Append(c);
            }
        }

        string clean = builder.ToString().Trim();
        if (clean.Length == 0)
        {
            clean = "customer";
        }

        return clean.Length <= maxLength ? clean : clean[..maxLength].TrimEnd();
    }
}
