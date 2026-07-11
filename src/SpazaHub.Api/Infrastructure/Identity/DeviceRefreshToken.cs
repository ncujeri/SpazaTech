namespace SpazaHub.Api.Identity;

/// <summary>
/// Long-lived refresh token bound to a registered device. Stored hashed. Owner access
/// tokens are minted from it, and cashier PIN logins are only accepted against a live
/// device token.
/// </summary>
public class DeviceRefreshToken
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 hash of the opaque token value. The raw token is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public Guid DeviceId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }
}
