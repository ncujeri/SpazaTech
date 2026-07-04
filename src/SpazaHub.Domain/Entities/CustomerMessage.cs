using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// One outbound message to a customer (payment reminder and similar). The Id doubles as
/// the client reference passed to the SMS provider so delivery webhooks map back without
/// lookups. Append-only aside from status transitions driven by delivery receipts.
/// </summary>
public class CustomerMessage : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>Template key the body was rendered from, e.g. reminder-en or reminder-zu.</summary>
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>Rendered body. Must fit one 160-character GSM-7 segment.</summary>
    public string Body { get; set; } = string.Empty;

    public MessageChannel Channel { get; set; }

    public MessageStatus Status { get; set; }

    public string? ProviderMessageId { get; set; }

    public decimal? Cost { get; set; }

    public DateTime QueuedAtUtc { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    public string? FailureReason { get; set; }
}
