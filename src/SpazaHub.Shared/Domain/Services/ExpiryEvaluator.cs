namespace SpazaHub.Domain.Services;

/// <summary>One goods-received batch of a product, as far as expiry is concerned.</summary>
public sealed record ExpiryBatch(DateOnly? ExpiryDate, decimal QuantityReceived, DateTime ReceivedAtUtc);

/// <summary>A batch estimated to still be on the shelf that is expired or expiring soon.</summary>
public sealed record ExpiryAlert(
    Guid ProductId,
    DateOnly ExpiryDate,
    decimal EstimatedUnitsAtRisk,
    bool IsExpired);

/// <summary>
/// Estimates which received batches are still on the shelf and flags the ones at or
/// near their best-before date. Shops rotate stock (oldest sold first), so the
/// remaining on-hand quantity is allocated to the newest batches; a fully sold old
/// batch never warns. This allocation is an estimate for warnings only. It has nothing
/// to do with costing, which stays weighted average.
/// </summary>
public static class ExpiryEvaluator
{
    public static IReadOnlyList<ExpiryAlert> Evaluate(
        Guid productId,
        decimal quantityOnHand,
        IReadOnlyList<ExpiryBatch> receivedBatches,
        DateOnly today,
        int warningDays)
    {
        if (warningDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warningDays), "Warning window cannot be negative.");
        }

        if (quantityOnHand <= 0m || receivedBatches.Count == 0)
        {
            return [];
        }

        // Oldest stock sells first, so what remains sits in the newest batches:
        // walk batches newest-first and hand out the on-hand quantity.
        decimal remainingToAllocate = quantityOnHand;
        var alerts = new List<ExpiryAlert>();
        DateOnly warningCutoff = today.AddDays(warningDays);

        foreach (var batch in receivedBatches.OrderByDescending(b => b.ReceivedAtUtc))
        {
            if (remainingToAllocate <= 0m)
            {
                break;
            }

            decimal inThisBatch = Math.Min(batch.QuantityReceived, remainingToAllocate);
            remainingToAllocate -= inThisBatch;

            if (batch.ExpiryDate is DateOnly expiry && inThisBatch > 0m && expiry <= warningCutoff)
            {
                alerts.Add(new ExpiryAlert(productId, expiry, inThisBatch, expiry < today));
            }
        }

        // Soonest expiry first; merge batches sharing the same date.
        return alerts
            .GroupBy(a => a.ExpiryDate)
            .Select(g => new ExpiryAlert(
                productId, g.Key, g.Sum(a => a.EstimatedUnitsAtRisk), g.First().IsExpired))
            .OrderBy(a => a.ExpiryDate)
            .ToList();
    }
}
