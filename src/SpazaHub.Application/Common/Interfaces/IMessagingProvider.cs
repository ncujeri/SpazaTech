namespace SpazaHub.Application.Common.Interfaces;

/// <summary>Outcome of an SMS submission to the provider.</summary>
public sealed record SmsSendResult(bool Accepted, string? ProviderMessageId, string? Error);

/// <summary>
/// Port for outbound SMS. Production adapter is SMSFlow (smsflow.co.za); development
/// uses a logging fake. The client reference passed on every send is the
/// CustomerMessage.Id so delivery webhooks map back without lookups.
/// </summary>
public interface IMessagingProvider
{
    Task<SmsSendResult> SendSmsAsync(
        string toPhoneE164,
        string body,
        string clientReference,
        CancellationToken cancellationToken = default);
}
