using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// Till cashback: customer draws drawer cash against a card or SASSA payment. The fee is
/// charged on top (customer receives the cash amount, the card is charged cash plus fee).
/// Card processing itself happens on the external terminal; this records the event for
/// reconciliation. Append-only.
/// </summary>
public class CashbackTransaction : Entity, ITenantOwned, IAppendOnly
{
    public Guid TenantId { get; set; }

    /// <summary>Null for standalone cashback with no basket.</summary>
    public Guid? SaleId { get; set; }

    public Guid? CustomerId { get; set; }

    /// <summary>Cash handed to the customer.</summary>
    public decimal CashOutAmount { get; set; }

    /// <summary>Fee rate snapshotted at transaction time so historical reports never shift.</summary>
    public decimal FeeRateApplied { get; set; }

    public FeeRoundingMode FeeRoundingApplied { get; set; }

    public decimal FeeAmount { get; set; }

    /// <summary>CashOutAmount plus FeeAmount: what the card terminal is charged.</summary>
    public decimal TotalCharged { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    public Guid CashierId { get; set; }

    /// <summary>Set when the amount met or exceeded the owner review threshold.</summary>
    public bool FlaggedForOwnerReview { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
