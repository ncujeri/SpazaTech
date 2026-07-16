using Microsoft.Extensions.Logging;
using SpazaHub.Api.Common.Interfaces;

namespace SpazaHub.Api.Messaging;

/// <summary>
/// Development stand-in for the SMSFlow adapter (Phase 7). Writes the message to the
/// log instead of sending it, so OTP flows are testable without an aggregator account.
/// </summary>
public class DevLogSmsProvider : IMessagingProvider
{
    private readonly ILogger<DevLogSmsProvider> _logger;

    public DevLogSmsProvider(ILogger<DevLogSmsProvider> logger)
    {
        _logger = logger;
    }

    public Task<SmsSendResult> SendSmsAsync(
        string toPhoneE164, string body, string clientReference, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "DEV SMS to {Phone} (ref {Reference}): {Body}", toPhoneE164, clientReference, body);
        return Task.FromResult(new SmsSendResult(true, $"dev-{Guid.NewGuid():N}", null));
    }
}
