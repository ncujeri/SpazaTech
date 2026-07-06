using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Domain.Entities;

namespace SpazaHub.Client.Services;

/// <summary>
/// Orders the quick-ring grid by local sales velocity: units sold over the trailing
/// window, computed from this device's local sale lines. Self-ordering, no server.
/// </summary>
public class QuickRingService
{
    public const int WindowDays = 14;

    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public QuickRingService(IDbContextFactory<ClientDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Top sellers first, then the rest alphabetically, so a new shop still sees its
    /// whole catalog on the grid.
    /// </summary>
    public async Task<IReadOnlyList<Product>> GetQuickRingProductsAsync(int take = 12)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();

        DateTime windowStart = DateTime.UtcNow.AddDays(-WindowDays);

        // Aggregate in memory: SQLite cannot sum decimals server-side.
        var soldRows = await db.SaleLines.AsNoTracking()
            .Join(db.Sales.AsNoTracking(),
                line => line.SaleId,
                sale => sale.Id,
                (line, sale) => new { line.ProductId, line.Quantity, sale.OccurredAtUtc })
            .Where(x => x.ProductId != null && x.OccurredAtUtc >= windowStart)
            .ToListAsync();

        var velocity = soldRows
            .GroupBy(x => x.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var products = await db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync();

        return products
            .OrderByDescending(p => velocity.GetValueOrDefault(p.Id))
            .ThenBy(p => p.Name)
            .Take(take)
            .ToList();
    }
}
