using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SpazaHub.Api.Common.Interfaces;

namespace SpazaHub.Api.Messaging;

/// <summary>SMSFlow (portal.smsflow.co.za) configuration. Credentials come from user-secrets or Key Vault.</summary>
public class SmsFlowOptions
{
    public const string SectionName = "SmsFlow";

    /// <summary>API root. The production host is https://portal.smsflow.co.za/ (trailing slash matters).</summary>
    public string BaseUrl { get; set; } = "https://portal.smsflow.co.za/";

    /// <summary>Basic-auth username for the token endpoint. Placeholder in appsettings; real value via secrets.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Basic-auth password for the token endpoint. Placeholder in appsettings; real value via secrets.</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Label SMSFlow records against each batch; shows up in their portal reporting.</summary>
    public string CampaignName { get; set; } = "SpazaHub";

    /// <summary>
    /// When true, SMSFlow suppresses sends to numbers on its own opt-out list. Left false so
    /// transactional messages (OTP login) always deliver; marketing consent is enforced in-app
    /// via Customer.ReminderConsent before a reminder is ever queued.
    /// </summary>
    public bool CheckOptOuts { get; set; }

    /// <summary>Shared secret SMSFlow includes on webhook calls, checked on every delivery.</summary>
    public string WebhookSecret { get; set; } = string.Empty;
}

/// <summary>
/// Caches the SMSFlow login token across sends. The token endpoint uses Basic auth and
/// returns a bearer token with a sliding expiry, so re-authenticating on every message
/// would be wasteful. Registered as a singleton; the send provider is scoped.
/// </summary>
public sealed class SmsFlowTokenCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private string? _token;
    private DateTimeOffset _expiresAtUtc;

    public SmsFlowTokenCache(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Drops the cached token so the next send re-authenticates (used after a 401).</summary>
    public void Invalidate()
    {
        _token = null;
    }

    /// <summary>
    /// Returns a valid bearer token, authenticating only when the cache is empty, expired, or
    /// the caller forces a refresh. <paramref name="authenticate"/> performs the Basic-auth call.
    /// </summary>
    public async Task<string> GetTokenAsync(
        Func<CancellationToken, Task<(string Token, int ExpiresInMinutes)>> authenticate,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (!forceRefresh && _token is not null && now < _expiresAtUtc)
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = _clock.GetUtcNow();
            if (!forceRefresh && _token is not null && now < _expiresAtUtc)
            {
                return _token;
            }

            (string token, int minutes) = await authenticate(cancellationToken);
            _token = token;
            // Refresh a minute early so a token never expires mid-request.
            _expiresAtUtc = now.AddMinutes(Math.Max(1, minutes - 1));
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// SMSFlow adapter behind IMessagingProvider. Two-step protocol: authenticate with the
/// Client ID / API secret over Basic auth to obtain a bearer token (cached), then POST the
/// message to the BulkMessages endpoint with that token. The typed HttpClient carries Polly
/// retry for transient errors and 429; an expired token (401) is refreshed and retried once.
/// </summary>
public class SmsFlowProvider : IMessagingProvider
{
    private const string AuthenticatePath = "api/integration/authentication";
    private const string BulkMessagesPath = "api/integration/BulkMessages";

    private readonly HttpClient _http;
    private readonly SmsFlowOptions _options;
    private readonly SmsFlowTokenCache _tokens;
    private readonly ILogger<SmsFlowProvider> _logger;

    public SmsFlowProvider(
        HttpClient http,
        IOptions<SmsFlowOptions> options,
        SmsFlowTokenCache tokens,
        ILogger<SmsFlowProvider> logger)
    {
        _http = http;
        _tokens = tokens;
        _logger = logger;
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException(
                "SmsFlow:ClientId and SmsFlow:ApiSecret are not configured. Use user-secrets locally or Key Vault in production.");
        }

        // A trailing slash is required for the relative paths to resolve under the API root.
        string baseUrl = _options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/";
        _http.BaseAddress = new Uri(baseUrl);
    }

    public async Task<SmsSendResult> SendSmsAsync(
        string toPhoneE164, string body, string clientReference, CancellationToken cancellationToken = default)
    {
        // SMSFlow wants the number in international format without the leading '+'.
        string destination = toPhoneE164.TrimStart('+');
        var request = new BulkRequest(
            new SendOptions(StartDeliveryUtc: null, _options.CampaignName, _options.CheckOptOuts),
            [new BulkMessage(body, destination)]);

        try
        {
            using HttpResponseMessage response = await PostBulkAsync(request, forceTokenRefresh: false, cancellationToken);

            // A stale cached token is the one failure worth an automatic retry: refresh and resend once.
            HttpResponseMessage effective = response;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _tokens.Invalidate();
                effective = await PostBulkAsync(request, forceTokenRefresh: true, cancellationToken);
            }

            using (effective == response ? null : effective)
            {
                if (!effective.IsSuccessStatusCode)
                {
                    string detail = await effective.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning(
                        "SMSFlow rejected message {Reference}: {Status} {Detail}",
                        clientReference, (int)effective.StatusCode, detail);
                    return new SmsSendResult(false, null, $"SMSFlow returned {(int)effective.StatusCode}.");
                }

                var payload = await effective.Content.ReadFromJsonAsync<BulkResponse>(cancellationToken);
                if (payload is null || payload.StatusCode != 200)
                {
                    string? firstError = payload?.Errors?.FirstOrDefault()?.Message;
                    _logger.LogWarning(
                        "SMSFlow reported a send failure for {Reference}: {Error}", clientReference, firstError);
                    return new SmsSendResult(false, null, firstError ?? "SMSFlow reported a send failure.");
                }

                // Batch-level id; SMSFlow does not return a per-message id for single sends.
                string? providerMessageId = payload.SendResponse?.EventId.ToString();
                return new SmsSendResult(true, providerMessageId, null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Retries already ran inside the handler pipeline; this is the final failure.
            _logger.LogError(ex, "SMSFlow send failed for {Reference} after retries.", clientReference);
            return new SmsSendResult(false, null, "Could not reach SMSFlow.");
        }
    }

    private async Task<HttpResponseMessage> PostBulkAsync(
        BulkRequest request, bool forceTokenRefresh, CancellationToken cancellationToken)
    {
        string token = await _tokens.GetTokenAsync(AuthenticateAsync, forceTokenRefresh, cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Post, BulkMessagesPath)
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(message, cancellationToken);
    }

    private async Task<(string Token, int ExpiresInMinutes)> AuthenticateAsync(CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, AuthenticatePath);
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ApiSecret}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken);
        if (payload is null || string.IsNullOrEmpty(payload.Token))
        {
            throw new InvalidOperationException("SMSFlow authentication returned no token.");
        }

        // A sliding expiry of 0 would be nonsensical; fall back to a short life so we still refresh.
        int minutes = payload.ExpiresInMinutes > 0 ? payload.ExpiresInMinutes : 5;
        return (payload.Token, minutes);
    }

    private sealed record AuthResponse(string? Token, int ExpiresInMinutes, string? Schema);

    private sealed record BulkRequest(
        [property: JsonPropertyName("SendOptions")] SendOptions SendOptions,
        [property: JsonPropertyName("messages")] IReadOnlyList<BulkMessage> Messages);

    private sealed record SendOptions(
        [property: JsonPropertyName("startDeliveryUtc")] DateTime? StartDeliveryUtc,
        [property: JsonPropertyName("campaignName")] string CampaignName,
        [property: JsonPropertyName("checkOptOuts")] bool CheckOptOuts);

    private sealed record BulkMessage(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("destination")] string Destination);

    private sealed record BulkResponse(int StatusCode, SendResponseBody? SendResponse, ApiError[]? Errors);

    private sealed record SendResponseBody(long EventId, int Messages, int Parts, decimal Cost, decimal RemainingBalance);

    private sealed record ApiError(string? Code, string? Message, string? Field);
}
