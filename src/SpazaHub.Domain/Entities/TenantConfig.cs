using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// Owner-configurable settings for one shop. Mutable reference data: syncs with
/// last-write-wins and a server-side audit of overwritten values.
/// </summary>
public class TenantConfig : Entity, ITenantOwned, IMutableSynced
{
    public Guid TenantId { get; set; }

    /// <summary>Cash tenders are rounded to this increment (default 10 cents).</summary>
    public decimal CashRoundingIncrement { get; set; } = 0.10m;

    /// <summary>Cashback fee rate charged on top of the cash drawn. Default 10 percent.</summary>
    public decimal CashbackFeeRate { get; set; } = 0.10m;

    public FeeRoundingMode CashbackFeeRounding { get; set; } = FeeRoundingMode.UpToRand;

    /// <summary>Hour (0-23, shop local time) at which the trading day rolls over. Default 04:00.</summary>
    public int TradingDayRollHour { get; set; } = 4;

    public decimal MaxCashbackPerTransaction { get; set; } = 500m;

    public decimal MaxCashbackPerDay { get; set; } = 2000m;

    /// <summary>Cashbacks at or above this amount are flagged for owner review.</summary>
    public decimal CashbackOwnerReviewThreshold { get; set; } = 200m;

    /// <summary>Cashback is blocked if projected drawer cash would fall below this floor.</summary>
    public decimal DrawerCashFloor { get; set; } = 100m;

    /// <summary>Whether cashiers may override a customer credit limit warning.</summary>
    public bool CashiersMayOverrideCreditLimit { get; set; }

    /// <summary>Conservative default: at most one automated reminder per debtor per week.</summary>
    public int MaxRemindersPerDebtorPerWeek { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
