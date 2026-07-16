using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Services;

namespace SpazaHub.Client.Services;

/// <summary>A line on the guided count sheet: what the book says should be there.</summary>
public sealed record CountSheetLine(Guid ProductId, string ProductName, decimal Expected, decimal UnitCost);

/// <summary>
/// Guided stock take. The sheet walks products by stock value (most money on the
/// shelf first), the corrections become append-only movements, and the variance
/// report puts a rand value on shrinkage.
/// </summary>
public class StockTakeService
{
    private readonly LocalStore _store;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public StockTakeService(LocalStore store, IDbContextFactory<ClientDbContext> contextFactory)
    {
        _store = store;
        _contextFactory = contextFactory;
    }

    /// <summary>Active products ordered by value on the shelf, highest first.</summary>
    public async Task<IReadOnlyList<CountSheetLine>> GetCountSheetAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var products = await db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync();

        return products
            .Select(p => new CountSheetLine(p.Id, p.Name, p.CachedQuantity, p.WeightedAverageCost))
            .OrderByDescending(l => l.Expected * l.UnitCost)
            .ThenBy(l => l.ProductName)
            .ToList();
    }

    /// <summary>
    /// Applies the counted sheet: writes StockTakeCorrection movements, resets cached
    /// quantities to the counted truth, and returns the variance report.
    /// </summary>
    public async Task<StockTakeResult> SubmitAsync(
        IReadOnlyList<StockCount> counts, Guid? cashierId = null)
    {
        var result = StockTakeCalculator.Build(counts, cashierId, DateTime.UtcNow);

        foreach (var correction in result.Corrections)
        {
            await _store.SaveLocalWriteAsync(correction);
        }

        if (result.Corrections.Count > 0)
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            foreach (var count in counts)
            {
                var product = await db.Products.FirstOrDefaultAsync(p => p.Id == count.ProductId);
                if (product is not null && product.CachedQuantity != count.Counted)
                {
                    product.CachedQuantity = count.Counted;
                }
            }

            await db.SaveChangesAsync();
        }

        return result;
    }
}
