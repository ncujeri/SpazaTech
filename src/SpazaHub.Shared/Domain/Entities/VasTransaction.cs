using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// A prepaid electricity, airtime, or data vend. Online-only by definition. The till
/// counts it only once Confirmed. On a lost response the client re-queries by
/// IdempotencyKey, never re-vends.
/// </summary>
public class VasTransaction : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public VasProductType ProductType { get; set; }

    public VasTransactionStatus Status { get; set; }

    /// <summary>Client-generated key sent with every vend so replays cannot double-vend.</summary>
    public Guid IdempotencyKey { get; set; }

    public decimal Amount { get; set; }

    public string? TargetReference { get; set; }

    public string? ProviderReference { get; set; }

    /// <summary>Voucher or token returned by the provider, e.g. the electricity token.</summary>
    public string? Token { get; set; }

    public Guid? CashierId { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public DateTime? ConfirmedAtUtc { get; set; }

    public string? FailureReason { get; set; }
}
