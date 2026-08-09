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

    /// <summary>
    /// The product a scanned barcode resolves to, plus how many units the scan represents
    /// (a case code moves several) and, for a scale-printed label, the price read out of the
    /// barcode itself.
    /// </summary>
    public sealed record BarcodeScan(Product Product, decimal Quantity, decimal? UnitPriceOverride);

    /// <summary>
    /// Resolves a scanned code to a product. Checks the barcode alias table first (unit,
    /// pack, and price-embedded codes), then falls back to the product's own primary barcode
    /// so shops set up before aliases existed keep working. Returns null for an unknown code.
    /// </summary>
    public async Task<BarcodeScan?> ScanBarcodeAsync(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        await using var db = await _contextFactory.CreateDbContextAsync();

        // 1) Exact alias match: a unit code (qty 1) or a case code (qty = pack size).
        var alias = await db.ProductBarcodes.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Code == barcode && b.IsActive);
        if (alias is not null)
        {
            var product = await ActiveProductAsync(db, alias.ProductId);
            if (product is not null)
            {
                decimal quantity = alias.Kind == BarcodeKind.Pack && alias.UnitsPerScan > 0m
                    ? alias.UnitsPerScan
                    : 1m;
                return new BarcodeScan(product, quantity, null);
            }
        }

        // 2) Scale-printed variable-measure label: match the constant item-reference prefix
        // and take the price from the barcode. Longest matching prefix wins.
        if (PriceEmbeddedBarcode.TryReadPrice(barcode, out decimal priceRands))
        {
            var priceEmbedded = await db.ProductBarcodes.AsNoTracking()
                .Where(b => b.Kind == BarcodeKind.PriceEmbedded && b.IsActive)
                .ToListAsync();
            var match = priceEmbedded
                .Where(b => barcode.StartsWith(b.Code, StringComparison.Ordinal))
                .OrderByDescending(b => b.Code.Length)
                .FirstOrDefault();
            if (match is not null)
            {
                var product = await ActiveProductAsync(db, match.ProductId);
                if (product is not null)
                {
                    return new BarcodeScan(product, 1m, priceRands);
                }
            }
        }

        // 3) Legacy primary barcode carried on the product row itself.
        var legacy = await db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Barcode == barcode && p.IsActive);
        return legacy is null ? null : new BarcodeScan(legacy, 1m, null);
    }

    private static async Task<Product?> ActiveProductAsync(ClientDbContext db, Guid productId)
        => await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && p.IsActive);

    /// <summary>Active barcode aliases for a product, oldest first.</summary>
    public async Task<IReadOnlyList<ProductBarcode>> GetBarcodesAsync(Guid productId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.ProductBarcodes.AsNoTracking()
            .Where(b => b.ProductId == productId && b.IsActive)
            .OrderBy(b => b.CreatedAtUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Adds a barcode alias to a product. For a price-embedded label only the constant
    /// item-reference portion is stored. Returns false without saving if the code already
    /// maps to another active product (a barcode identifies one item).
    /// </summary>
    public async Task<bool> AddBarcodeAsync(
        Guid productId, string code, BarcodeKind kind, decimal unitsPerScan = 1m)
    {
        string stored = kind == BarcodeKind.PriceEmbedded
            ? PriceEmbeddedBarcode.ItemReferenceOf(code) ?? code.Trim()
            : code.Trim();
        if (stored.Length == 0)
        {
            return false;
        }

        await using (var db = await _contextFactory.CreateDbContextAsync())
        {
            bool clash = await db.ProductBarcodes.AsNoTracking()
                .AnyAsync(b => b.Code == stored && b.IsActive && b.ProductId != productId);
            if (clash)
            {
                return false;
            }
        }

        var now = DateTime.UtcNow;
        await _store.SaveLocalWriteAsync(new ProductBarcode
        {
            ProductId = productId,
            Code = stored,
            Kind = kind,
            UnitsPerScan = kind == BarcodeKind.Pack ? unitsPerScan : 1m,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        return true;
    }

    /// <summary>Soft-deletes a barcode alias so the removal syncs to other devices.</summary>
    public async Task RemoveBarcodeAsync(Guid barcodeId)
    {
        ProductBarcode? barcode;
        await using (var db = await _contextFactory.CreateDbContextAsync())
        {
            barcode = await db.ProductBarcodes.AsNoTracking().FirstOrDefaultAsync(b => b.Id == barcodeId);
        }

        if (barcode is null || !barcode.IsActive)
        {
            return;
        }

        barcode.IsActive = false;
        barcode.UpdatedAtUtc = DateTime.UtcNow;
        await _store.SaveLocalWriteAsync(barcode);
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
