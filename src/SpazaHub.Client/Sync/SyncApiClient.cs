using System.Net.Http.Headers;
using System.Net.Http.Json;
using SpazaHub.Shared.Sync;

namespace SpazaHub.Client.Sync;

/// <summary>Holds the current access token for API calls. Filled by the login flow.</summary>
public class AccessTokenStore
{
    public string? AccessToken { get; set; }

    public bool HasToken => !string.IsNullOrEmpty(AccessToken);
}

/// <summary>Thin HTTP wrapper over the server sync endpoints.</summary>
public class SyncApiClient
{
    private readonly HttpClient _http;
    private readonly AccessTokenStore _tokens;

    public SyncApiClient(HttpClient http, AccessTokenStore tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<SyncPushResponse?> PushAsync(
        SyncPushRequest request, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/sync/push")
        {
            Content = JsonContent.Create(request)
        };
        Authorize(message);

        using var response = await _http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncPushResponse>(cancellationToken);
    }

    public async Task<SyncPullResponse?> PullAsync(
        long cursor, int pageSize, Guid deviceId, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"api/sync/pull?cursor={cursor}&pageSize={pageSize}&deviceId={deviceId}");
        Authorize(message);

        using var response = await _http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncPullResponse>(cancellationToken);
    }

    private void Authorize(HttpRequestMessage message)
    {
        if (_tokens.HasToken)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
        }
    }
}
