using Microsoft.EntityFrameworkCore;
using SpazaHub.Api.Persistence;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Api.Messaging;

/// <summary>
/// Applies SMSFlow webhook callbacks. Runs without a tenant context (webhooks are not
/// tenant-authenticated), so lookups go by message id or phone across tenants with
/// filters ignored; every change flows back down to devices via the change log.
/// </summary>
public class SmsWebhookService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<SmsWebhookService> _logger;

    public SmsWebhookService(AppDbContext db, TimeProvider clock, ILogger<SmsWebhookService> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Delivery receipt: the client reference is the CustomerMessage id. Unknown
    /// references are ignored (webhooks retry; never 500 on noise).
    /// </summary>
    public async Task<bool> ApplyDeliveryReceiptAsync(
        string clientReference, string status, string? providerMessageId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(clientReference, out Guid messageId))
        {
            return false;
        }

        var message = await _db.CustomerMessages.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null)
        {
            _logger.LogWarning("Delivery receipt for unknown message {Reference}.", clientReference);
            return false;
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        message.ProviderMessageId ??= providerMessageId;
        message.UpdatedAtUtc = now;

        switch (status.Trim().ToLowerInvariant())
        {
            case "delivered":
                message.Status = MessageStatus.Delivered;
                message.DeliveredAtUtc = now;
                break;
            case "failed":
            case "undelivered":
                message.Status = MessageStatus.Failed;
                message.FailureReason = $"Provider reported {status}.";
                break;
            default:
                message.Status = MessageStatus.Submitted;
                break;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Inbound reply. STOP (and common variants) withdraws reminder consent for every
    /// customer with that phone number, per POPIA. Anything else is ignored for now.
    /// </summary>
    public async Task<int> ApplyInboundAsync(
        string fromPhoneE164, string text, CancellationToken cancellationToken = default)
    {
        string trimmed = text.Trim().ToUpperInvariant();
        bool isOptOut = trimmed is "STOP" or "END" or "UNSUBSCRIBE" or "STOP ALL";
        if (!isOptOut)
        {
            return 0;
        }

        var customers = await _db.Customers.IgnoreQueryFilters()
            .Where(c => c.Phone == fromPhoneE164 && c.ReminderConsent)
            .ToListAsync(cancellationToken);

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        foreach (var customer in customers)
        {
            customer.ReminderConsent = false;
            customer.ConsentCapturedAtUtc = null;
            customer.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Opt-out from {Phone}: {Count} customer(s) unsubscribed.", fromPhoneE164, customers.Count);
        return customers.Count;
    }
}
