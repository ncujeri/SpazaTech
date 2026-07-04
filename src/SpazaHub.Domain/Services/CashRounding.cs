namespace SpazaHub.Domain.Services;

/// <summary>
/// Rounds cash tenders to the shop's configured increment (default 10 cents) and reports
/// the rounding difference explicitly so cash-up balances.
/// </summary>
public static class CashRounding
{
    /// <summary>
    /// Rounds an amount to the nearest multiple of the increment (half away from zero).
    /// Returns the rounded amount and the signed adjustment (rounded minus original).
    /// An increment of zero or less means no rounding.
    /// </summary>
    public static (decimal RoundedAmount, decimal Adjustment) RoundCash(decimal amount, decimal increment)
    {
        if (increment <= 0m)
        {
            return (amount, 0m);
        }

        decimal rounded = Math.Round(amount / increment, 0, MidpointRounding.AwayFromZero) * increment;
        return (rounded, rounded - amount);
    }
}
