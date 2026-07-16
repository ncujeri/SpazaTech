using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SpazaHub.Api.Common.Interfaces;

namespace SpazaHub.Api.Messaging;

/// <summary>SMSFlow (smsflow.co.za) configuration. The API key comes from user-secrets or Key Vault.</summary>
public class SmsFlowOptions
{
    public const string SectionName = "SmsFlow";

    public string BaseUrl { get; set; } = "https://api.smsflow.co.za/";

    /// <summary>Placeholder in appsettings; real value via user-secrets or Key Vault.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Sender { get; set; } = "SpazaHub";

    /// <summary>Shared secret SMSFlow includes on webhook calls, checked on every delivery.</summary>
    public string WebhookSecret { get; set; } = string.Empty;
}

/// <summary>
/// SMSFlow adapter behind IMessagingProvider. The typed HttpClient carries Polly retry
/// with exponential backoff (transient errors and 429). CustomerMessage.Id travels as
/// the client reference on every send so delivery webhooks map back without lookups.
/// </summary>
public class SmsFlowProvider : IMessagingProvider
{
    private sealed record SendRequest(string To, string Body, string From, string ClientReference);

    private sealed record SendResponse(string? MessageId, string? Status);

    private readonly HttpClient _http;
    private readonly ILogger<SmsFlowProvider> _logger;

    public SmsFlowProvider(HttpClient http, IOptions<SmsFlowOptions> options, ILogger<SmsFlowProvider> logger)
    {
        _http = http;
        _logger = logger;

        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException(
                "SmsFlow:ApiKey is not configured. Use user-secrets locally or Key Vault in production.");
        }

        _http.BaseAddress = new Uri(config.BaseUrl);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public async Task<SmsSendResult> SendSmsAsync(
        string toPhoneE164, string body, string clientReference, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "v1/messages",
                new SendRequest(toPhoneE164, body, "SpazaHub", clientReference),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "SMSFlow rejected message {Reference}: {Status} {Detail}",
                    clientReference, (int)response.StatusCode, detail);
                return new SmsSendResult(false, null, $"SMSFlow returned {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<SendResponse>(cancellationToken);
            return new SmsSendResult(true, payload?.MessageId, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Retries already ran inside the handler pipeline; this is the final failure.
            _logger.LogError(ex, "SMSFlow send failed for {Reference} after retries.", clientReference);
            return new SmsSendResult(false, null, "Could not reach SMSFlow.");
        }
    }
}
