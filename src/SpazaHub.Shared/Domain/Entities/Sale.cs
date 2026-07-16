using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// A completed sale. Immutable once written: refunds and voids are new compensating
/// sales referencing the original via ReversesSaleId.
/// </summary>
public class Sale : Entity, ITenantOwned, IAppendOnly
{
    public Guid TenantId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid? CashierId { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Sum of line totals before cash rounding.</summary>
    public decimal Total { get; set; }

    /// <summary>
    /// Difference introduced by rounding cash tenders to the configured increment,
    /// recorded explicitly so cash-up balances. Signed: negative when rounded down.
    /// </summary>
    public decimal CashRoundingAdjustment { get; set; }

    /// <summary>Set when this sale is a refund or void compensating an earlier sale.</summary>
    public Guid? ReversesSaleId { get; set; }

    public List<SaleLine> Lines { get; set; } = [];

    public List<SalePayment> Payments { get; set; } = [];
}
