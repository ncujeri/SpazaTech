using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Services;

namespace SpazaHub.Client.Services;

/// <summary>A product that dropped to or below its low-stock threshold during a sale.</summary>
public sealed record LowStockAlert(Guid ProductId, string Name, decimal QuantityLeft, decimal Threshold);

/// <summary>
/// Completes sales entirely locally: builds the immutable sale graph, writes every
/// entity through the outbox in parent-first order, updates cached quantities, and
/// reports low-stock hits. No network is touched on this path.
/// </summary>
public class PosService
{
    private readonly LocalStore _store;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public PosService(LocalStore store, IDbContextFactory<ClientDbContext> contextFactory)
    {
        _store = store;
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Rings up the cart. Returns the completed sale plus any low-stock alerts to
    /// surface as local notifications.
    /// </summary>
    public async Task<(CompletedSale Sale, IReadOnlyList<LowStockAlert> Alerts)> CompleteSaleAsync(
        IReadOnlyList<CartLine> cart,
        IReadOnlyList<TenderInput> nonCashTenders,
        Guid? cashierId = null)
    {
        var state = await _store.GetStateAsync()
            ?? throw new InvalidOperationException("Device not set up yet.");

        decimal increment = await GetCashRoundingIncrementAsync();

        var completed = SaleBuilder.Build(
            cart, nonCashTenders, increment, state.DeviceId, cashierId, DateTime.UtcNow);

        foreach (var entity in completed.InWriteOrder())
        {
            await _store.SaveLocalWriteAsync(entity);
        }

        var alerts = await ApplyQuantityChangesAsync(completed);
        return (completed, alerts);
    }

    /// <summary>Voids a sale in full via a compensating sale referencing the original.</summary>
    public async Task<CompletedSale> VoidSaleAsync(Guid saleId, Guid? cashierId = null)
    {
        var state = await _store.GetStateAsync()
            ?? throw new InvalidOperationException("Device not set up yet.");

        Sale original;
        List<SaleLine> lines;
        List<SalePayment> payments;
        await using (var db = await _contextFactory.CreateDbContextAsync())
        {
            original = await db.Sales.AsNoTracking().FirstAsync(s => s.Id == saleId);

            bool alreadyVoided = await db.Sales.AnyAsync(s => s.ReversesSaleId == saleId);
            if (alreadyVoided)
            {
                throw new InvalidOperationException("This sale is already voided.");
            }

            lines = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == saleId).ToListAsync();
            payments = await db.SalePayments.AsNoTracking().Where(p => p.SaleId == saleId).ToListAsync();
        }

        var reversal = SaleBuilder.BuildFullReversal(
            original, lines, payments, state.DeviceId, cashierId, DateTime.UtcNow);

        foreach (var entity in reversal.InWriteOrder())
        {
            await _store.SaveLocalWriteAsync(entity);
        }

        await ApplyQuantityChangesAsync(reversal);
        return reversal;
    }

    /// <summary>Today's sales, newest first, for the recent-sales and void screen.</summary>
    public async Task<IReadOnlyList<Sale>> GetRecentSalesAsync(int take = 30)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.Sales.AsNoTracking()
            .OrderByDescending(s => s.OccurredAtUtc)
            .Take(take)
            .ToListAsync();
    }

    private async Task<decimal> GetCashRoundingIncrementAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var config = await db.TenantConfigs.AsNoTracking().FirstOrDefaultAsync();
        return config?.CashRoundingIncrement ?? 0.10m;
    }

    /// <summary>
    /// Recomputes CachedQuantity for every product the sale touched and returns the
    /// ones that crossed their low-stock threshold.
    /// </summary>
    private async Task<IReadOnlyList<LowStockAlert>> ApplyQuantityChangesAsync(CompletedSale completed)
    {
        var productIds = completed.StockMovements.Select(m => m.ProductId).Distinct().ToList();
        if (productIds.Count == 0)
        {
            return [];
        }

        var alerts = new List<LowStockAlert>();
        await using var db = await _contextFactory.CreateDbContextAsync();

        foreach (Guid productId in productIds)
        {
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId);
            if (product is null)
            {
                continue;
            }

            // SQLite cannot aggregate decimals server-side; sum in memory.
            var quantities = await db.StockMovements
                .Where(m => m.ProductId == productId)
                .Select(m => m.Quantity)
                .ToListAsync();
            product.CachedQuantity = quantities.Sum();

            if (product.LowStockThreshold > 0m && product.CachedQuantity <= product.LowStockThreshold)
            {
                alerts.Add(new LowStockAlert(
                    product.Id, product.Name, product.CachedQuantity, product.LowStockThreshold));
            }
        }

        await db.SaveChangesAsync();
        return alerts;
    }
}
