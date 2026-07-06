using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Domain.Entities;
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
}
