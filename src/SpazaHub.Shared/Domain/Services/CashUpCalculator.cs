using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Services;

/// <summary>
/// Drawer reconciliation math. Expected cash is the sum of every cash movement in the
/// trading day window: opening float, cash sales in, refunds and cashback out,
/// payouts and expenses out. Movements carry their own signs, so expected cash is a
/// straight sum; variance is what the cashier counted minus what should be there.
/// </summary>
public static class CashUpCalculator
{
    public static decimal ComputeExpectedCash(IEnumerable<CashMovement> tradingDayMovements)
        => tradingDayMovements.Sum(m => m.Amount);

    public static decimal ComputeVariance(decimal declaredCash, decimal expectedCash)
        => declaredCash - expectedCash;

    /// <summary>Current drawer cash: same sum, usable mid-day for the cashback floor check.</summary>
    public static decimal ComputeDrawerCash(IEnumerable<CashMovement> tradingDayMovements)
        => ComputeExpectedCash(tradingDayMovements);

    /// <summary>Total cashback cash paid out so far today, as a positive number.</summary>
    public static decimal ComputeCashbackPaidToday(IEnumerable<CashMovement> tradingDayMovements)
        => -tradingDayMovements
            .Where(m => m.Type == CashMovementType.CashbackPaid)
            .Sum(m => m.Amount);
}
