using System.Net.Http.Json;
using SpazaHub.Client.Sync;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Client.Services;

/// <summary>
/// Login flow against the server plus local session state. The device refresh token is
/// kept in memory for this phase; durable token storage lands with the full auth UX.
/// </summary>
public class AuthApiClient
{
    private readonly HttpClient _http;
    private readonly AccessTokenStore _tokens;
    private readonly LocalStore _store;

    public AuthApiClient(HttpClient http, AccessTokenStore tokens, LocalStore store)
    {
        _http = http;
        _tokens = tokens;
        _store = store;
    }

    public Guid? TenantId { get; private set; }

    public Guid? DeviceId { get; private set; }

    public string? RefreshToken { get; private set; }

    public bool IsSetUp => TenantId is not null;

    private string _pendingShopName = string.Empty;

    public async Task<bool> RequestOtpAsync(string phone, string shopName)
    {
        _pendingShopName = shopName;
        using var response = await _http.PostAsJsonAsync(
            "api/auth/register-owner", new RegisterOwnerRequest(phone, shopName));
        return response.IsSuccessStatusCode;
    }

    /// <summary>Verifies the OTP, stores tokens, and initializes the local database.</summary>
    public async Task<bool> VerifyOtpAsync(string phone, string code, string deviceName)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/auth/verify-otp", new VerifyOtpRequest(phone, code, deviceName));
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var tokens = await response.Content.ReadFromJsonAsync<AuthTokensResponse>();
        if (tokens is null)
        {
            return false;
        }

        _tokens.AccessToken = tokens.AccessToken;
        RefreshToken = tokens.RefreshToken;
        TenantId = tokens.TenantId;
        DeviceId = tokens.DeviceId;

        await _store.InitializeAsync(tokens.TenantId, tokens.DeviceId, _pendingShopName);
        return true;
    }

    /// <summary>
    /// Offline demo setup: initializes the local database with generated ids so the
    /// POS works with no server at all. Sync stays idle until a real login happens.
    /// </summary>
    public async Task StartDemoModeAsync()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        TenantId = tenantId;
        DeviceId = deviceId;

        await _store.InitializeAsync(tenantId, deviceId, "Demo Spaza");
    }

    /// <summary>Restores session state from the local database after a reload.</summary>
    public async Task TryRestoreAsync()
    {
        var state = await _store.GetStateAsync();
        if (state is not null)
        {
            TenantId = state.TenantId;
            DeviceId = state.DeviceId;
        }
    }
}
