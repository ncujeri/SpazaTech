namespace SpazaHub.Infrastructure.Auth;

/// <summary>JWT issuing configuration. SigningKey comes from user-secrets or Key Vault.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "SpazaHub";

    public string Audience { get; set; } = "SpazaHub";

    /// <summary>HMAC signing key. Placeholder in appsettings; real value via user-secrets or Key Vault.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int OwnerAccessTokenMinutes { get; set; } = 60;

    /// <summary>Cashier tokens cover one shift.</summary>
    public int CashierAccessTokenMinutes { get; set; } = 720;

    public int DeviceRefreshTokenDays { get; set; } = 90;
}

/// <summary>OTP and PIN policy configuration.</summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    public int OtpLifetimeSeconds { get; set; } = 300;

    public int MaxOtpAttempts { get; set; } = 5;

    public int MaxPinAttempts { get; set; } = 5;

    public int PinLockoutMinutes { get; set; } = 15;
}
