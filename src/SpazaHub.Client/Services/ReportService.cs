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

    /// <summary>The shop's name for report headers; empty string if not yet set up.</summary>
    public async Task<string> GetShopNameAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var state = await db.SyncState.AsNoTracking().FirstOrDefaultAsync();
        return state?.ShopName ?? string.Empty;
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

    /// <summary>
    /// The order list for a cash-and-carry trip: what to buy, how much, and the estimated
    /// spend per line (suggested quantity at the product's last cost) plus the trip total,
    /// so the owner knows how much cash to take. Built on the same velocity as the trip list.
    /// </summary>
    public async Task<OrderList> GetOrderListAsync(int windowDays = 14, int daysToCover = 7)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();

        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync();
        DateTime windowStart = DateTime.UtcNow.AddDays(-windowDays);

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

        var costByProduct = products.ToDictionary(p => p.Id, p => p.WeightedAverageCost);
        var lines = ReorderAdvisor.BuildTripList(activity, daysToCover)
            .Select(s =>
            {
                decimal unitCost = costByProduct.TryGetValue(s.ProductId, out var c) ? c : 0m;
                return new OrderListLine(
                    s.ProductName, s.OnHand, s.UnitsPerDay, s.SuggestedQuantity,
                    unitCost, Math.Round(s.SuggestedQuantity * unitCost, 2));
            })
            .ToList();

        return new OrderList(
            lines, lines.Sum(l => l.EstimatedCost), lines.Count, daysToCover);
    }

    /// <summary>
    /// Stock on hand and what it is worth: for every active product the quantity on the
    /// shelf, its value at cost (weighted average) and at retail (sell price), with totals
    /// and the potential margin locked up in stock. Highest cost value first.
    /// </summary>
    public async Task<StockValuationReport> GetStockValuationAsync(bool includeZeroStock = false)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();

        var products = await db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync();

        var lines = products
            .Where(p => includeZeroStock || p.CachedQuantity != 0m)
            .Select(p => new StockValuationLine(
                p.Name,
                p.CachedQuantity,
                p.WeightedAverageCost,
                Math.Round(p.CachedQuantity * p.WeightedAverageCost, 2),
                p.SellPrice,
                Math.Round(p.CachedQuantity * p.SellPrice, 2)))
            .OrderByDescending(l => l.CostValue)
            .ThenBy(l => l.ProductName)
            .ToList();

        return new StockValuationReport(
            lines,
            lines.Sum(l => l.CostValue),
            lines.Sum(l => l.RetailValue),
            lines.Count,
            products.Count(p => p.CachedQuantity <= 0m));
    }

    /// <summary>
    /// Which products are moving and which are dead money. For every active product:
    /// units sold over the trailing window, the daily rate, days of cover left on the
    /// shelf, and a band (Fast / Steady / Slow / Not moving) relative to the best seller.
    /// Ranked fastest first. Drives the movement charts and the printable report.
    /// </summary>
    public async Task<ProductMovementReport> GetProductMovementAsync(int windowDays = 30)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();

        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync();
        DateTime windowStart = DateTime.UtcNow.AddDays(-windowDays);
        DateTime nowUtc = DateTime.UtcNow;

        // Sale movements carry negative quantities; aggregate in memory for SQLite.
        var saleMovements = await db.StockMovements.AsNoTracking()
            .Where(m => m.Type == StockMovementType.Sale)
            .Select(m => new { m.ProductId, m.Quantity, m.OccurredAtUtc })
            .ToListAsync();

        var byProduct = saleMovements
            .GroupBy(m => m.ProductId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var raw = products.Select(p =>
        {
            byProduct.TryGetValue(p.Id, out var mine);
            mine ??= new();
            decimal sold = -mine.Where(m => m.OccurredAtUtc >= windowStart).Sum(m => m.Quantity);
            if (sold < 0m) sold = 0m; // returns can outweigh sales for a stale product
            DateTime? lastSale = mine.Count > 0 ? mine.Max(m => m.OccurredAtUtc) : null;
            decimal perDay = windowDays > 0 ? sold / windowDays : 0m;
            double? daysCover = perDay > 0m && p.CachedQuantity > 0m
                ? (double)(p.CachedQuantity / perDay)
                : (double?)null;
            int? daysSince = lastSale.HasValue
                ? (int)Math.Floor((nowUtc - lastSale.Value).TotalDays)
                : (int?)null;
            return new
            {
                p.Name,
                Sold = sold,
                PerDay = Math.Round(perDay, 2),
                p.CachedQuantity,
                p.SellPrice,
                Revenue = Math.Round(sold * p.SellPrice, 2),
                DeadValueAtCost = Math.Round(p.CachedQuantity * p.WeightedAverageCost, 2),
                DaysCover = daysCover,
                DaysSince = daysSince,
                LastSale = lastSale,
            };
        }).ToList();

        decimal topSold = raw.Count > 0 ? raw.Max(r => r.Sold) : 0m;

        var lines = raw
            .Select(r =>
            {
                double share = topSold > 0m ? (double)(r.Sold / topSold) : 0d;
                MovementBand band = r.Sold <= 0m ? MovementBand.NotMoving
                    : share >= 0.5 ? MovementBand.Fast
                    : share >= 0.2 ? MovementBand.Steady
                    : MovementBand.Slow;
                return new ProductMovementLine(
                    r.Name, r.Sold, r.PerDay, r.CachedQuantity, r.SellPrice,
                    r.Revenue, r.DeadValueAtCost, r.DaysCover, r.DaysSince, share, band);
            })
            .OrderByDescending(l => l.UnitsSold)
            .ThenByDescending(l => l.OnHand)
            .ThenBy(l => l.ProductName)
            .ToList();

        decimal deadValue = lines
            .Where(l => l.Band == MovementBand.NotMoving && l.OnHand > 0m)
            .Sum(l => l.OnHandCostValue);

        return new ProductMovementReport(
            lines,
            windowDays,
            lines.Sum(l => l.UnitsSold),
            lines.Sum(l => l.RevenueInWindow),
            lines.Count(l => l.Band == MovementBand.Fast),
            lines.Count(l => l.Band == MovementBand.Steady),
            lines.Count(l => l.Band == MovementBand.Slow),
            lines.Count(l => l.Band == MovementBand.NotMoving && l.OnHand > 0m),
            deadValue);
    }
}

/// <summary>One line on the cash-and-carry order list, with estimated spend.</summary>
public sealed record OrderListLine(
    string ProductName, decimal OnHand, decimal UnitsPerDay,
    decimal SuggestedQuantity, decimal UnitCost, decimal EstimatedCost);

/// <summary>The full order list plus the estimated cash to take on the trip.</summary>
public sealed record OrderList(
    IReadOnlyList<OrderListLine> Lines, decimal TotalEstimatedCost, int ItemCount, int DaysToCover);

/// <summary>One product's stock on hand valued at cost and at retail.</summary>
public sealed record StockValuationLine(
    string ProductName, decimal OnHand, decimal UnitCost,
    decimal CostValue, decimal SellPrice, decimal RetailValue);

/// <summary>Stock valuation across the catalog, with totals and locked-up margin.</summary>
public sealed record StockValuationReport(
    IReadOnlyList<StockValuationLine> Lines,
    decimal TotalCostValue, decimal TotalRetailValue, int LineCount, int OutOfStockCount)
{
    /// <summary>Gross margin sitting in stock: retail value minus cost value.</summary>
    public decimal PotentialMargin => TotalRetailValue - TotalCostValue;
}

/// <summary>How well a product is selling, relative to the shop's best seller.</summary>
public enum MovementBand { Fast, Steady, Slow, NotMoving }

/// <summary>One product's movement over the window: velocity, cover, and its band.</summary>
public sealed record ProductMovementLine(
    string ProductName, decimal UnitsSold, decimal UnitsPerDay, decimal OnHand,
    decimal SellPrice, decimal RevenueInWindow, decimal OnHandCostValue,
    double? DaysCover, int? DaysSinceLastSale, double ShareOfTop, MovementBand Band);

/// <summary>Movement across the catalog with band counts and dead-money value.</summary>
public sealed record ProductMovementReport(
    IReadOnlyList<ProductMovementLine> Lines,
    int WindowDays, decimal TotalUnitsSold, decimal TotalRevenue,
    int FastCount, int SteadyCount, int SlowCount, int NotMovingCount,
    decimal DeadStockValueAtCost);
