using System.Net.Http.Json;
using SpazaHub.Client.Sync;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Client.Services;

/// <summary>
/// Login flow and durable device session. The device refresh token persists across
/// reloads (TokenStorage); on boot it is exchanged for a fresh access token when there
/// is signal, and the shop keeps trading locally when there is not.
/// </summary>
public class AuthApiClient
{
    private readonly HttpClient _http;
    private readonly AccessTokenStore _tokens;
    private readonly LocalStore _store;
    private readonly TokenStorage _storage;
    private readonly ILogger<AuthApiClient> _logger;
    private bool _restoreAttempted;

    public AuthApiClient(
        HttpClient http, AccessTokenStore tokens, LocalStore store,
        TokenStorage storage, ILogger<AuthApiClient> logger)
    {
        _http = http;
        _tokens = tokens;
        _store = store;
        _storage = storage;
        _logger = logger;
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

    /// <summary>Verifies the OTP, persists the session, and initializes the local database.</summary>
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
        await _storage.SaveAsync(new StoredSession(
            tokens.RefreshToken, tokens.TenantId, tokens.DeviceId, _pendingShopName));
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
        await _storage.SaveAsync(new StoredSession(null, tenantId, deviceId, "Demo Spaza"));
    }

    /// <summary>
    /// Restores the session after a reload: local database first (offline truth), then
    /// the stored refresh token, then a best-effort access token refresh when online.
    /// Trading never waits on the network.
    /// </summary>
    public async Task TryRestoreAsync()
    {
        var state = await _store.GetStateAsync();
        if (state is not null)
        {
            TenantId = state.TenantId;
            DeviceId = state.DeviceId;
        }

        if (_restoreAttempted)
        {
            return;
        }

        _restoreAttempted = true;

        var stored = await _storage.LoadAsync();
        if (stored is null)
        {
            return;
        }

        TenantId ??= stored.TenantId;
        DeviceId ??= stored.DeviceId;
        RefreshToken = stored.RefreshToken;

        if (RefreshToken is null || _tokens.HasToken)
        {
            return;
        }

        await RefreshAccessTokenAsync();
    }

    /// <summary>
    /// Exchanges the device refresh token for a fresh access token. Called on boot and
    /// by the sync loop when the hourly access token expires. Offline failures are
    /// silent; a server rejection drops the dead token and pauses sync until sign-in.
    /// </summary>
    public async Task<bool> RefreshAccessTokenAsync()
    {
        if (RefreshToken is null)
        {
            return false;
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(
                "api/auth/refresh", new RefreshTokenRequest(RefreshToken));
            if (response.IsSuccessStatusCode)
            {
                var tokens = await response.Content.ReadFromJsonAsync<AuthTokensResponse>();
                if (tokens is not null)
                {
                    _tokens.AccessToken = tokens.AccessToken;
                    return true;
                }

                return false;
            }

            // Revoked or expired device: keep trading locally, drop the dead token.
            RefreshToken = null;
            var stored = await _storage.LoadAsync();
            if (stored is not null)
            {
                await _storage.SaveAsync(stored with { RefreshToken = null });
            }

            _tokens.AccessToken = null;
            _logger.LogWarning("Device session was rejected by the server; sync is paused until sign-in.");
            return false;
        }
        catch (Exception ex)
        {
            // Offline: normal life, try again next boot or next sync cycle.
            _logger.LogDebug(ex, "Token refresh skipped: {Message}", ex.Message);
            return false;
        }
    }
}
