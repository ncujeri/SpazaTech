using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Client.Services;

/// <summary>
/// Daily report assembled from local rows for the chosen trading day. Runs against
/// SQLite first per the spec; server-side equivalents come with owner remote views.
/// </summary>
public class ReportService
{
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public ReportService(IDbContextFactory<ClientDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<DailySummary> GetDailySummaryAsync(DateOnly tradingDate)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();

        var config = await db.TenantConfigs.AsNoTracking().FirstOrDefaultAsync() ?? new TenantConfig();
        var (startUtc, endUtc) = TradingDay.GetUtcWindow(tradingDate, config.TradingDayRollHour);

        var sales = await db.Sales.AsNoTracking()
            .Where(s => s.OccurredAtUtc >= startUtc && s.OccurredAtUtc < endUtc)
            .ToListAsync();
        var saleIds = sales.Select(s => s.Id).ToList();

        var lines = await db.SaleLines.AsNoTracking()
            .Where(l => saleIds.Contains(l.SaleId))
            .ToListAsync();
        var payments = await db.SalePayments.AsNoTracking()
            .Where(p => saleIds.Contains(p.SaleId))
            .ToListAsync();

        var cashbacks = await db.CashbackTransactions.AsNoTracking()
            .Where(c => c.OccurredAtUtc >= startUtc && c.OccurredAtUtc < endUtc)
            .ToListAsync();
        var fees = await db.FeeIncomes.AsNoTracking()
            .Where(f => f.OccurredAtUtc >= startUtc && f.OccurredAtUtc < endUtc)
            .ToListAsync();
        var movements = await db.CashMovements.AsNoTracking()
            .Where(m => m.OccurredAtUtc >= startUtc && m.OccurredAtUtc < endUtc)
            .ToListAsync();

        return DailyReportCalculator.Build(
            tradingDate, sales, lines, payments, cashbacks, fees, movements);
    }

    /// <summary>
    /// The cash-and-carry trip list plus dead stock, from local sales velocity over
    /// the trailing window.
    /// </summary>
    public async Task<(IReadOnlyList<ReorderSuggestion> TripList, IReadOnlyList<DeadStockItem> DeadStock)>
        GetTripAdviceAsync(int windowDays = 14, int daysToCover = 7, int deadAfterDays = 30)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();

        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync();
        DateTime windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Sale movements carry negative quantities; aggregate in memory for SQLite.
        var saleMovements = await db.StockMovements.AsNoTracking()
            .Where(m => m.Type == StockMovementType.Sale)
            .Select(m => new { m.ProductId, m.Quantity, m.OccurredAtUtc })
            .ToListAsync();

        var activity = products.Select(p =>
        {
            var mine = saleMovements.Where(m => m.ProductId == p.Id).ToList();
            decimal soldInWindow = -mine.Where(m => m.OccurredAtUtc >= windowStart).Sum(m => m.Quantity);
            DateTime? lastSale = mine.Count > 0 ? mine.Max(m => m.OccurredAtUtc) : null;
            return new ProductActivity(
                p.Id, p.Name, p.CachedQuantity, p.LowStockThreshold, soldInWindow, windowDays, lastSale);
        }).ToList();

        return (
            ReorderAdvisor.BuildTripList(activity, daysToCover),
            ReorderAdvisor.FindDeadStock(activity, DateTime.UtcNow, deadAfterDays));
    }
}
