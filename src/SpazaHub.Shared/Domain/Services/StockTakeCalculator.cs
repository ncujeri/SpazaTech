using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Services;

/// <summary>One counted line in a guided stock take.</summary>
public sealed record StockCount(Guid ProductId, string ProductName, decimal Expected, decimal Counted, decimal UnitCost);

/// <summary>One product's variance after a stock take. Negative units means shrinkage.</summary>
public sealed record StockVariance(
    Guid ProductId, string ProductName, decimal Expected, decimal Counted,
    decimal VarianceUnits, decimal VarianceValue);

/// <summary>The stock take outcome: correction movements to write plus the variance report.</summary>
public sealed record StockTakeResult(
    IReadOnlyList<StockMovement> Corrections,
    IReadOnlyList<StockVariance> Variances,
    decimal TotalShrinkageValue)
{
    public bool HasVariances => Variances.Count > 0;
}

/// <summary>
/// Turns a counted stock sheet into StockTakeCorrection movements and a variance
/// (shrinkage) report valued at cost. Matching counts produce nothing; the movement
/// stream stays clean of zero rows.
/// </summary>
public static class StockTakeCalculator
{
    public static StockTakeResult Build(
        IReadOnlyList<StockCount> counts, Guid? cashierId, DateTime occurredAtUtc)
    {
        var corrections = new List<StockMovement>();
        var variances = new List<StockVariance>();

        foreach (var count in counts)
        {
            decimal delta = count.Counted - count.Expected;
            if (delta == 0m)
            {
                continue;
            }

            corrections.Add(new StockMovement
            {
                ProductId = count.ProductId,
                Type = StockMovementType.StockTakeCorrection,
                Quantity = delta,
                CashierId = cashierId,
                Note = $"Stock take: counted {count.Counted:0.###}, expected {count.Expected:0.###}",
                OccurredAtUtc = occurredAtUtc
            });

            variances.Add(new StockVariance(
                count.ProductId, count.ProductName, count.Expected, count.Counted,
                delta, Math.Round(delta * count.UnitCost, 2, MidpointRounding.AwayFromZero)));
        }

        // Shrinkage is the value of what went missing (negative variances only).
        decimal shrinkage = -variances.Where(v => v.VarianceValue < 0m).Sum(v => v.VarianceValue);

        return new StockTakeResult(corrections, variances, shrinkage);
    }
}
