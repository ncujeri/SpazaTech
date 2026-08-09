using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Client.Services;

/// <summary>
/// Seeds a believable spaza catalog with a sale history straight into the local SQLite,
/// so the owner reports (movement, stock value, order list) have something to show when
/// testing on a fresh device. Writes bypass the sync outbox on purpose: demo rows stay on
/// this device and never push to the server. Intended for local testing only.
/// </summary>
public class DemoDataService
{
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;
    private readonly OpfsDbPersistence _persistence;

    public DemoDataService(IDbContextFactory<ClientDbContext> contextFactory, OpfsDbPersistence persistence)
    {
        _contextFactory = contextFactory;
        _persistence = persistence;
    }

    public async Task<int> ProductCountAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.Products.CountAsync();
    }

    /// <summary>One catalog line to fabricate, with its target sales pace.</summary>
    /// <param name="PerDay">Average units sold per day; 0 means it does not sell.</param>
    /// <param name="EndOnHand">Units to leave on the shelf after the seeded sales.</param>
    /// <param name="DeadDaysAgo">If set, a single sale this many days ago and nothing since.</param>
    private sealed record Spec(
        string Name, decimal Sell, decimal Cost, double PerDay,
        decimal EndOnHand, decimal LowThreshold, int? DeadDaysAgo = null);

    private static readonly Spec[] Catalog =
    {
        // Fast movers
        new("White Bread",          16.00m, 11.50m, 10.0, 12m,  8m),
        new("Coca-Cola 500ml",      14.00m,  9.00m,  7.0, 30m, 12m),
        new("Simba Chips 125g",     12.50m,  8.20m,  5.0, 40m, 15m),
        // Steady
        new("Lucky Star Pilchards", 22.90m, 16.50m,  4.0, 25m,  6m),
        new("Maize Meal 2kg",       34.90m, 27.00m,  3.0, 20m,  6m),
        new("White Sugar 1kg",      24.90m, 18.50m,  2.5, 18m,  6m),
        new("Fresh Milk 1L",        18.00m, 13.00m,  2.0,  6m,  6m),
        // Slow
        new("Eggs (6-pack)",        21.00m, 16.00m,  1.5, 24m,  6m),
        new("Baked Beans 410g",     15.50m, 11.00m,  1.0, 30m,  6m),
        new("Box of Matches",        4.00m,  2.30m,  0.7, 40m, 10m),
        new("Handy Andy 750ml",     28.90m, 21.00m,  0.35, 12m, 3m),
        // Not moving / dead money
        new("Shoe Polish Black",    22.00m, 15.50m,  0.0,  9m,  3m),
        new("Canned Peaches 410g",  26.50m, 19.00m,  0.0, 15m,  4m),
        new("Instant Coffee 100g",  45.00m, 34.00m,  0.0,  8m,  3m, DeadDaysAgo: 42),
    };

    /// <summary>
    /// Loads the demo catalog with <paramref name="historyDays"/> of sales. No-ops with a
    /// message if the local database already holds products, so it never doubles up or
    /// tramples real data.
    /// </summary>
    public async Task<string> SeedAsync(int historyDays = 45)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();

        if (await db.Products.AnyAsync())
        {
            return "Local database already has products. Clear it first if you want fresh demo data.";
        }

        var state = await db.SyncState.AsNoTracking().FirstOrDefaultAsync();
        Guid tenantId = state?.TenantId ?? Guid.NewGuid();
        var rng = new Random(4242); // fixed seed: repeatable demo
        DateTime now = DateTime.UtcNow;
        DateTime historyStart = now.AddDays(-historyDays);

        var products = new List<Product>(Catalog.Length);
        var movements = new List<StockMovement>();

        foreach (var spec in Catalog)
        {
            var product = new Product
            {
                TenantId = tenantId,
                Name = spec.Name,
                SellPrice = spec.Sell,
                WeightedAverageCost = spec.Cost,
                LowStockThreshold = spec.LowThreshold,
                IsActive = true,
                CreatedAtUtc = historyStart,
                UpdatedAtUtc = now,
            };

            decimal totalSold = 0m;

            if (spec.DeadDaysAgo is int deadDays && deadDays <= historyDays)
            {
                DateTime when = now.AddDays(-deadDays);
                movements.Add(Sale(product.Id, tenantId, 1m, when));
                totalSold = 1m;
            }
            else if (spec.PerDay > 0)
            {
                for (int d = historyDays - 1; d >= 0; d--)
                {
                    int qty = SamplePoisson(rng, spec.PerDay);
                    if (qty <= 0)
                    {
                        continue;
                    }

                    DateTime when = now.AddDays(-d).AddHours(-rng.Next(0, 11));
                    movements.Add(Sale(product.Id, tenantId, qty, when));
                    totalSold += qty;
                }
            }

            // One receipt at the start big enough to cover every sale and leave the target
            // shelf quantity, so on-hand lands exactly on EndOnHand.
            decimal received = totalSold + spec.EndOnHand;
            movements.Add(new StockMovement
            {
                TenantId = tenantId,
                ProductId = product.Id,
                Type = StockMovementType.GoodsReceived,
                Quantity = received,
                UnitCost = spec.Cost,
                OccurredAtUtc = historyStart,
            });

            product.CachedQuantity = received - totalSold; // == spec.EndOnHand
            products.Add(product);
        }

        db.Products.AddRange(products);
        db.StockMovements.AddRange(movements);
        await db.SaveChangesAsync();
        await _persistence.SaveAsync();

        return $"Loaded {products.Count} products and {movements.Count} stock movements over the last {historyDays} days.";
    }

    /// <summary>
    /// Wipes local products and stock movements so demo data can be reloaded. Leaves sync
    /// state and everything else untouched. Destructive — the page guards it behind a
    /// confirmation.
    /// </summary>
    public async Task<string> ClearAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        int movements = await db.StockMovements.ExecuteDeleteAsync();
        int products = await db.Products.ExecuteDeleteAsync();
        await _persistence.SaveAsync();
        return $"Cleared {products} products and {movements} stock movements.";
    }

    private static StockMovement Sale(Guid productId, Guid tenantId, decimal units, DateTime whenUtc) => new()
    {
        TenantId = tenantId,
        ProductId = productId,
        Type = StockMovementType.Sale,
        Quantity = -units, // sales carry negative quantities
        OccurredAtUtc = whenUtc,
    };

    /// <summary>Knuth's Poisson sampler — daily unit counts that vary around the mean.</summary>
    private static int SamplePoisson(Random rng, double lambda)
    {
        double l = Math.Exp(-lambda);
        int k = 0;
        double p = 1.0;
        do
        {
            k++;
            p *= rng.NextDouble();
        }
        while (p > l);
        return k - 1;
    }
}
