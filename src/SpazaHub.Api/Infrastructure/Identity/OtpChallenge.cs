namespace SpazaHub.Api.Identity;

/// <summary>
/// One-time password challenge for owner phone verification. The code is stored hashed;
/// the raw code travels by SMS only.
/// </summary>
public class OtpChallenge
{
    public Guid Id { get; set; }

    /// <summary>Phone in E.164 format.</summary>
    public string Phone { get; set; } = string.Empty;

    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Shop name captured at registration, applied when the tenant is created.</summary>
    public string ShopName { get; set; } = string.Empty;

    public int FailedAttempts { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? ConsumedAtUtc { get; set; }
}
