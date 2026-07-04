using SpazaHub.Shared.Auth;

namespace SpazaHub.Application.Common.Interfaces;

/// <summary>
/// Port over identity, OTP, and token plumbing. Implemented in Infrastructure with
/// ASP.NET Core Identity and JWT so Application stays free of framework dependencies.
/// </summary>
public interface IAuthService
{
    /// <summary>Starts owner registration or returning-owner login by sending an OTP.</summary>
    Task<RegisterOwnerResponse> RegisterOwnerAsync(
        string phoneE164, string shopName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the OTP. Creates the tenant and owner account on first verification,
    /// registers the device, and returns an owner access token plus a long-lived
    /// device refresh token.
    /// </summary>
    Task<AuthTokensResponse> VerifyOtpAsync(
        string phoneE164, string code, string deviceName, CancellationToken cancellationToken = default);

    /// <summary>Exchanges a live device refresh token for a fresh owner access token.</summary>
    Task<AuthTokensResponse> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shift login: validates the device refresh token, checks the cashier PIN with
    /// lockout, and returns a short-lived cashier access token.
    /// </summary>
    Task<AuthTokensResponse> CashierLoginAsync(
        string deviceRefreshToken, Guid cashierId, string pin, CancellationToken cancellationToken = default);

    /// <summary>Creates a cashier sub-user in the current tenant. Owner only.</summary>
    Task<CashierDto> CreateCashierAsync(
        string name, string pin, bool canDoCashback, CancellationToken cancellationToken = default);

    /// <summary>Lists cashiers in the current tenant for the shift login screen.</summary>
    Task<IReadOnlyList<CashierDto>> ListCashiersAsync(CancellationToken cancellationToken = default);
}
