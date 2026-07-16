namespace SpazaHub.Domain.Services;

/// <summary>What the advisor knows about one product's recent life.</summary>
public sealed record ProductActivity(
    Guid ProductId,
    string ProductName,
    decimal OnHand,
    decimal LowStockThreshold,
    decimal UnitsSoldInWindow,
    int WindowDays,
    DateTime? LastSaleAtUtc);

/// <summary>A line on the cash-and-carry trip list.</summary>
public sealed record ReorderSuggestion(
    Guid ProductId, string ProductName, decimal OnHand, decimal UnitsPerDay, decimal SuggestedQuantity);

/// <summary>Stock that is not moving: candidates for markdown or dropping.</summary>
public sealed record DeadStockItem(Guid ProductId, string ProductName, decimal OnHand, DateTime? LastSaleAtUtc);

/// <summary>
/// FMCG buying advice from local sales velocity. The trip list answers the owner's
/// weekly question at the cash and carry: what do I buy, and how much, so I neither
/// run out nor bury cash in stock that does not move.
/// </summary>
public static class ReorderAdvisor
{
    /// <summary>
    /// Products whose stock will not cover the next trip cycle. Suggested quantity
    /// tops the shelf up to daysToCover of sales, rounded up to whole units.
    /// </summary>
    public static IReadOnlyList<ReorderSuggestion> BuildTripList(
        IReadOnlyList<ProductActivity> products, int daysToCover = 7)
    {
        if (daysToCover <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(daysToCover));
        }

        var suggestions = new List<ReorderSuggestion>();

        foreach (var product in products)
        {
            decimal unitsPerDay = product.WindowDays > 0
                ? product.UnitsSoldInWindow / product.WindowDays
                : 0m;

            decimal needed = Math.Ceiling(unitsPerDay * daysToCover - product.OnHand);
            bool belowThreshold = product.LowStockThreshold > 0m && product.OnHand <= product.LowStockThreshold;

            if (needed > 0m || belowThreshold)
            {
                decimal suggested = Math.Max(needed, belowThreshold ? Math.Max(1m, needed) : needed);
                suggestions.Add(new ReorderSuggestion(
                    product.ProductId, product.ProductName, product.OnHand,
                    Math.Round(unitsPerDay, 2), suggested));
            }
        }

        return suggestions
            .OrderByDescending(s => s.UnitsPerDay)
            .ThenBy(s => s.ProductName)
            .ToList();
    }

    /// <summary>Products with stock on the shelf and no sale in the dead window.</summary>
    public static IReadOnlyList<DeadStockItem> FindDeadStock(
        IReadOnlyList<ProductActivity> products, DateTime nowUtc, int deadAfterDays = 30)
    {
        DateTime cutoff = nowUtc.AddDays(-deadAfterDays);

        return products
            .Where(p => p.OnHand > 0m && (p.LastSaleAtUtc is null || p.LastSaleAtUtc < cutoff))
            .Select(p => new DeadStockItem(p.ProductId, p.ProductName, p.OnHand, p.LastSaleAtUtc))
            .OrderBy(p => p.LastSaleAtUtc ?? DateTime.MinValue)
            .ToList();
    }
}
