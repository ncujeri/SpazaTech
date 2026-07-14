using Microsoft.Extensions.Options;
using SpazaHub.Api.Messaging;

namespace SpazaHub.Api.Endpoints;

/// <summary>
/// SMSFlow callbacks. Authenticated by the shared webhook secret header, never by JWT.
/// Always answers 200 for authenticated calls so the provider does not retry forever;
/// unknown references are logged and dropped.
/// </summary>
public static class WebhookEndpoints
{
    public sealed record DeliveryPayload(string ClientReference, string Status, string? MessageId);

    public sealed record InboundPayload(string From, string Message);

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhooks/smsflow").WithTags("Webhooks");

        group.MapPost("/status", async (
            DeliveryPayload payload, HttpRequest request,
            SmsWebhookService webhooks, IOptions<SmsFlowOptions> options, CancellationToken ct) =>
        {
            if (!IsAuthentic(request, options.Value))
            {
                return Results.Unauthorized();
            }

            await webhooks.ApplyDeliveryReceiptAsync(payload.ClientReference, payload.Status, payload.MessageId, ct);
            return Results.Ok();
        });

        group.MapPost("/inbound", async (
            InboundPayload payload, HttpRequest request,
            SmsWebhookService webhooks, IOptions<SmsFlowOptions> options, CancellationToken ct) =>
        {
            if (!IsAuthentic(request, options.Value))
            {
                return Results.Unauthorized();
            }

            await webhooks.ApplyInboundAsync(payload.From, payload.Message, ct);
            return Results.Ok();
        });

        return app;
    }

    private static bool IsAuthentic(HttpRequest request, SmsFlowOptions options)
        => !string.IsNullOrEmpty(options.WebhookSecret)
           && request.Headers.TryGetValue("X-Webhook-Secret", out var secret)
           && string.Equals(secret.ToString(), options.WebhookSecret, StringComparison.Ordinal);
}
