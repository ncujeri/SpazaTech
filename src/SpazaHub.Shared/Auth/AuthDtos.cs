namespace SpazaHub.Shared.Auth;

/// <summary>Starts owner registration or login: sends an OTP to the phone.</summary>
public sealed record RegisterOwnerRequest(string Phone, string ShopName);

/// <summary>Acknowledges that an OTP was sent. The code itself travels by SMS only.</summary>
public sealed record RegisterOwnerResponse(bool OtpSent, int ExpiresInSeconds);

/// <summary>Verifies the OTP and registers this device to the tenant.</summary>
public sealed record VerifyOtpRequest(string Phone, string Code, string DeviceName);

/// <summary>Exchanges a device refresh token for a fresh owner access token.</summary>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <summary>Cashier shift login: 4-digit PIN checked against the registered device token.</summary>
public sealed record CashierLoginRequest(string DeviceRefreshToken, Guid CashierId, string Pin);

/// <summary>Tokens returned after successful authentication.</summary>
public sealed record AuthTokensResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string? RefreshToken,
    Guid TenantId,
    string Role,
    Guid? CashierId);

/// <summary>Owner creates a cashier sub-user with a 4-digit PIN.</summary>
public sealed record CreateCashierRequest(string Name, string Pin, bool CanDoCashback);

public sealed record CashierDto(Guid Id, string Name, bool IsActive, bool CanDoCashback);
