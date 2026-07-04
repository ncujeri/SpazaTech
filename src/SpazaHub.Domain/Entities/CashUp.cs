using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// End-of-day drawer reconciliation. Expected cash = opening float + cash sales
/// + cashback fees taken in cash - cashback paid out - payouts and expenses.
/// </summary>
public class CashUp : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Trading day this cash-up covers. Day boundary is tenant-configurable.</summary>
    public DateOnly TradingDate { get; set; }

    public Guid? DeviceId { get; set; }

    public decimal OpeningFloat { get; set; }

    /// <summary>Cash physically counted and declared by the cashier.</summary>
    public decimal DeclaredCash { get; set; }

    /// <summary>Computed from cash movements for the trading day.</summary>
    public decimal ExpectedCash { get; set; }

    /// <summary>DeclaredCash minus ExpectedCash. Negative means the drawer is short.</summary>
    public decimal Variance { get; set; }

    public Guid? CashierId { get; set; }

    public DateTime? CashierSignedAtUtc { get; set; }

    public DateTime? OwnerSignedAtUtc { get; set; }

    public string? Notes { get; set; }
}
