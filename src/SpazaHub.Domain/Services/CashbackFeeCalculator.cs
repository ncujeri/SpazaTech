using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Services;

/// <summary>Result of a cashback fee calculation, shown to the cashier before confirm.</summary>
public readonly record struct CashbackQuote(decimal CashOut, decimal Fee, decimal TotalCharged);

/// <summary>
/// Computes the cashback fee charged on top of the cash drawn. Default policy:
/// 10 percent of the cashback amount, rounded up to the nearest rand.
/// Customer receives R100, fee R10, card charged R110.
/// </summary>
public static class CashbackFeeCalculator
{
    /// <summary>
    /// Quotes a cashback: fee = cashOut * feeRate, rounded per the configured mode,
    /// total charged = cashOut + fee.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when cashOut is not positive or feeRate is negative.
    /// </exception>
    public static CashbackQuote Quote(decimal cashOut, decimal feeRate, FeeRoundingMode rounding)
    {
        if (cashOut <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(cashOut), "Cashback amount must be positive.");
        }

        if (feeRate < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(feeRate), "Fee rate cannot be negative.");
        }

        decimal rawFee = cashOut * feeRate;
        decimal fee = rounding switch
        {
            FeeRoundingMode.NearestRand => Math.Round(rawFee, 0, MidpointRounding.AwayFromZero),
            FeeRoundingMode.UpToRand => Math.Ceiling(rawFee),
            FeeRoundingMode.Exact => Math.Round(rawFee, 2, MidpointRounding.AwayFromZero),
            _ => throw new ArgumentOutOfRangeException(nameof(rounding), rounding, "Unknown fee rounding mode.")
        };

        return new CashbackQuote(cashOut, fee, cashOut + fee);
    }
}
