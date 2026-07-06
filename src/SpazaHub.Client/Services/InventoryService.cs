using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Client.Services;

/// <summary>
/// Product catalog and stock operations, all local-first. Goods received maintains the
/// weighted average cost; adjustments and wastage are explicit movement types so the
/// variance report can tell shrinkage from corrections.
/// </summary>
public class InventoryService
{
    private readonly LocalStore _store;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public InventoryService(LocalStore store, IDbContextFactory<ClientDbContext> contextFactory)
    {
        _store = store;
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<Product>> GetProductsAsync(bool includeInactive = false)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var query = db.Products.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        return await query.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Product?> FindByBarcodeAsync(string barcode)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Barcode == barcode && p.IsActive);
    }

    /// <summary>Creates or updates a product. Prices and thresholds are LWW reference data.</summary>
    public async Task SaveProductAsync(Product product)
    {
        product.UpdatedAtUtc = DateTime.UtcNow;
        if (product.CreatedAtUtc == default)
        {
            product.CreatedAtUtc = product.UpdatedAtUtc;
        }

        await _store.SaveLocalWriteAsync(product);
    }

    /// <summary>
    /// Receives stock: captures the cost and optional batch expiry date on the
    /// movement, moves the weighted average cost, and bumps the cached quantity.
    /// </summary>
    public async Task<Product> ReceiveGoodsAsync(
        Guid productId, decimal quantity, decimal unitCost,
        DateOnly? expiryDate = null, Guid? cashierId = null)
    {
        var product = await RequireProductAsync(productId);

        product.WeightedAverageCost = WeightedAverageCost.Apply(
            product.CachedQuantity, product.WeightedAverageCost, quantity, unitCost);
        product.CachedQuantity += quantity;
        product.UpdatedAtUtc = DateTime.UtcNow;

        await _store.SaveLocalWriteAsync(new StockMovement
        {
            ProductId = productId,
            Type = StockMovementType.GoodsReceived,
            Quantity = quantity,
            UnitCost = unitCost,
            ExpiryDate = expiryDate,
            CashierId = cashierId,
            OccurredAtUtc = DateTime.UtcNow
        });
        await _store.SaveLocalWriteAsync(product);

        return product;
    }

    /// <summary>A product batch that is expired or expiring soon, with its name for display.</summary>
    public sealed record NamedExpiryAlert(string ProductName, ExpiryAlert Alert);

    /// <summary>
    /// Batches estimated to still be on the shelf that are past or near their
    /// best-before date, soonest first. The window is owner-configurable.
    /// </summary>
    public async Task<IReadOnlyList<NamedExpiryAlert>> GetExpiryAlertsAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();

        var config = await db.TenantConfigs.AsNoTracking().FirstOrDefaultAsync() ?? new TenantConfig();
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        var products = await db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.CachedQuantity > 0)
            .ToListAsync();
        if (products.Count == 0)
        {
            return [];
        }

        var productIds = products.Select(p => p.Id).ToList();
        var receivedRows = await db.StockMovements.AsNoTracking()
            .Where(m => m.Type == StockMovementType.GoodsReceived
                && m.ExpiryDate != null
                && productIds.Contains(m.ProductId))
            .ToListAsync();

        var withExpiry = receivedRows.Select(m => m.ProductId).ToHashSet();
        var alerts = new List<NamedExpiryAlert>();

        foreach (var product in products.Where(p => withExpiry.Contains(p.Id)))
        {
            // All batches count for shelf allocation, dated or not.
            var batchRows = await db.StockMovements.AsNoTracking()
                .Where(m => m.Type == StockMovementType.GoodsReceived && m.ProductId == product.Id)
                .ToListAsync();
            var batches = batchRows
                .Select(m => new ExpiryBatch(m.ExpiryDate, m.Quantity, m.OccurredAtUtc))
                .ToList();

            foreach (var alert in ExpiryEvaluator.Evaluate(
                product.Id, product.CachedQuantity, batches, today, config.ExpiryWarningDays))
            {
                alerts.Add(new NamedExpiryAlert(product.Name, alert));
            }
        }

        return alerts.OrderBy(a => a.Alert.ExpiryDate).ToList();
    }

    /// <summary>Manual correction or wastage. Quantity is signed; wastage is negative.</summary>
    public async Task<Product> AdjustStockAsync(
        Guid productId, decimal signedQuantity, StockMovementType type, string? note, Guid? cashierId = null)
    {
        if (type is not (StockMovementType.Adjustment or StockMovementType.Wastage or StockMovementType.StockTakeCorrection))
        {
            throw new InvalidOperationException("Use ReceiveGoodsAsync for goods received and sales flow for sales.");
        }

        var product = await RequireProductAsync(productId);
        product.CachedQuantity += signedQuantity;
        product.UpdatedAtUtc = DateTime.UtcNow;

        await _store.SaveLocalWriteAsync(new StockMovement
        {
            ProductId = productId,
            Type = type,
            Quantity = signedQuantity,
            Note = note,
            CashierId = cashierId,
            OccurredAtUtc = DateTime.UtcNow
        });
        await _store.SaveLocalWriteAsync(product);

        return product;
    }

    private async Task<Product> RequireProductAsync(Guid productId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId)
            ?? throw new InvalidOperationException("Product not found.");
    }
}
