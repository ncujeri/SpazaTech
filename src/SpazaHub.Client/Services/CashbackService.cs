using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Client.Services;

/// <summary>
/// Till cashback, fully local: quotes show all three numbers before confirm, the
/// controls read today's drawer state from the local database, and the event stream
/// (transaction, drawer cash out, fee income) goes through the outbox. The card itself
/// is charged on the external terminal.
/// </summary>
public class CashbackService
{
    private readonly LocalStore _store;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public CashbackService(LocalStore store, IDbContextFactory<ClientDbContext> contextFactory)
    {
        _store = store;
        _contextFactory = contextFactory;
    }

    /// <summary>Quote for the confirm screen: cash out, fee, and total card charge.</summary>
    public async Task<CashbackQuote> GetQuoteAsync(decimal cashOut)
    {
        var config = await GetConfigAsync();
        return CashbackFeeCalculator.Quote(cashOut, config.CashbackFeeRate, config.CashbackFeeRounding);
    }

    /// <summary>Runs every control and reports why a cashback would be refused.</summary>
    public async Task<CashbackDenialReason> CheckAsync(
        decimal cashOut, PaymentMethod method, Cashier? cashier = null)
    {
        var context = await BuildContextAsync(cashier);
        return CashbackBuilder.Check(cashOut, method, context);
    }

    /// <summary>Completes the cashback and writes its event stream through the outbox.</summary>
    public async Task<CashbackEvents> CompleteCashbackAsync(
        decimal cashOut, PaymentMethod method, Guid? cashierId = null, Guid? customerId = null)
    {
        Cashier? cashier = null;
        if (cashierId is not null)
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            cashier = await db.Cashiers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cashierId);
        }

        var context = await BuildContextAsync(cashier);
        var events = CashbackBuilder.Build(
            cashOut, method, context, cashierId ?? Guid.Empty, DateTime.UtcNow, saleId: null, customerId);

        foreach (var entity in events.InWriteOrder())
        {
            await _store.SaveLocalWriteAsync(entity);
        }

        return events;
    }

    private async Task<TenantConfig> GetConfigAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.TenantConfigs.AsNoTracking().FirstOrDefaultAsync() ?? new TenantConfig();
    }

    private async Task<CashbackContext> BuildContextAsync(Cashier? cashier)
    {
        var config = await GetConfigAsync();

        await using var db = await _contextFactory.CreateDbContextAsync();
        var (startUtc, endUtc) = TradingDay.GetUtcWindow(
            TradingDay.GetTradingDate(DateTime.UtcNow, config.TradingDayRollHour),
            config.TradingDayRollHour);

        var todayMovements = await db.CashMovements.AsNoTracking()
            .Where(m => m.OccurredAtUtc >= startUtc && m.OccurredAtUtc < endUtc)
            .ToListAsync();

        return new CashbackContext(
            FeeRate: config.CashbackFeeRate,
            FeeRounding: config.CashbackFeeRounding,
            MaxPerTransaction: config.MaxCashbackPerTransaction,
            MaxPerDay: config.MaxCashbackPerDay,
            OwnerReviewThreshold: config.CashbackOwnerReviewThreshold,
            DrawerCashFloor: config.DrawerCashFloor,
            CashbackAlreadyPaidToday: CashUpCalculator.ComputeCashbackPaidToday(todayMovements),
            DrawerCashNow: CashUpCalculator.ComputeDrawerCash(todayMovements),
            CashierMayDoCashback: cashier?.CanDoCashback ?? true);
    }
}
