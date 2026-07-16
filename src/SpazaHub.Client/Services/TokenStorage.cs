using System.Text.Json;
using Microsoft.JSInterop;

namespace SpazaHub.Client.Services;

/// <summary>What survives a reload: enough to resume syncing without a new OTP.</summary>
public sealed record StoredSession(
    string? RefreshToken,
    Guid TenantId,
    Guid DeviceId,
    string ShopName);

/// <summary>
/// Persists the device session in localStorage so a page reload or phone restart does
/// not silently stop sync. The refresh token is device-bound and revocable server-side;
/// the short-lived access token is never persisted.
/// </summary>
public class TokenStorage
{
    private const string Key = "spazahub.session";

    private readonly IJSRuntime _js;
    private readonly ILogger<TokenStorage> _logger;

    public TokenStorage(IJSRuntime js, ILogger<TokenStorage> logger)
    {
        _js = js;
        _logger = logger;
    }

    public async Task SaveAsync(StoredSession session)
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", Key, JsonSerializer.Serialize(session));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the session; a reload will need a new sign-in.");
        }
    }

    public async Task<StoredSession?> LoadAsync()
    {
        try
        {
            string? json = await _js.InvokeAsync<string?>("localStorage.getItem", Key);
            return json is null ? null : JsonSerializer.Deserialize<StoredSession>(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No stored session available.");
            return null;
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", Key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clear the stored session.");
        }
    }
}
