using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Client.Services;

/// <summary>
/// The trading day drawer lifecycle: open with a float, record payouts and expenses,
/// then count and sign off. Expected cash always derives from the cash movement
/// stream; the CashUp row records the reconciliation, it never invents numbers.
/// </summary>
public class CashUpService
{
    private readonly LocalStore _store;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public CashUpService(LocalStore store, IDbContextFactory<ClientDbContext> contextFactory)
    {
        _store = store;
        _contextFactory = contextFactory;
    }

    public async Task<DateOnly> GetCurrentTradingDateAsync()
    {
        var config = await GetConfigAsync();
        return TradingDay.GetTradingDate(DateTime.UtcNow, config.TradingDayRollHour);
    }

    public async Task<CashUp?> GetCashUpAsync(DateOnly tradingDate)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.CashUps.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TradingDate == tradingDate);
    }

    /// <summary>Opens the trading day: records the float as both a movement and on the cash-up.</summary>
    public async Task<CashUp> StartDayAsync(decimal openingFloat, Guid? deviceId = null)
    {
        DateOnly tradingDate = await GetCurrentTradingDateAsync();

        if (await GetCashUpAsync(tradingDate) is not null)
        {
            throw new InvalidOperationException("Today's drawer is already open.");
        }

        var cashUp = new CashUp
        {
            TradingDate = tradingDate,
            OpeningFloat = openingFloat,
            DeviceId = deviceId,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await _store.SaveLocalWriteAsync(cashUp);

        if (openingFloat != 0m)
        {
            await _store.SaveLocalWriteAsync(new CashMovement
            {
                Type = CashMovementType.OpeningFloat,
                Amount = openingFloat,
                OccurredAtUtc = DateTime.UtcNow
            });
        }

        return cashUp;
    }

    /// <summary>Cash leaving the drawer outside a sale: supplier payout or shop expense.</summary>
    public async Task RecordCashOutAsync(decimal amount, CashMovementType type, string? note, Guid? cashierId = null)
    {
        if (amount <= 0m)
        {
            throw new InvalidOperationException("Amount must be positive.");
        }

        if (type is not (CashMovementType.Payout or CashMovementType.Expense or CashMovementType.BankDrop))
        {
            throw new InvalidOperationException("Only payouts, expenses, and bank drops are recorded here.");
        }

        await _store.SaveLocalWriteAsync(new CashMovement
        {
            Type = type,
            Amount = -amount,
            Note = note,
            CashierId = cashierId,
            OccurredAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Puts a mistaken money-out back in the drawer: a compensating positive movement
    /// of the same type, never a deleted row.
    /// </summary>
    public async Task UndoCashOutAsync(
        decimal amount, CashMovementType type, Guid? cashierId = null)
    {
        if (amount <= 0m)
        {
            throw new InvalidOperationException("Amount must be positive.");
        }

        if (type is not (CashMovementType.Payout or CashMovementType.Expense or CashMovementType.BankDrop))
        {
            throw new InvalidOperationException("Only payouts, expenses, and bank drops can be undone here.");
        }

        await _store.SaveLocalWriteAsync(new CashMovement
        {
            Type = type,
            Amount = amount,
            Note = "Undo",
            CashierId = cashierId,
            OccurredAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>Live drawer cash for the current trading day.</summary>
    public async Task<decimal> GetDrawerCashAsync()
    {
        var movements = await GetTradingDayMovementsAsync(await GetCurrentTradingDateAsync());
        return CashUpCalculator.ComputeDrawerCash(movements);
    }

    /// <summary>
    /// Counts the drawer: computes expected cash from the movement stream, records the
    /// declared amount, variance, and the cashier sign-off.
    /// </summary>
    public async Task<CashUp> CompleteCashUpAsync(decimal declaredCash, Guid? cashierId = null)
    {
        DateOnly tradingDate = await GetCurrentTradingDateAsync();
        var cashUp = await GetCashUpAsync(tradingDate)
            ?? throw new InvalidOperationException("Open the drawer with a float first.");

        var movements = await GetTradingDayMovementsAsync(tradingDate);
        decimal expected = CashUpCalculator.ComputeExpectedCash(movements);

        cashUp.DeclaredCash = declaredCash;
        cashUp.ExpectedCash = expected;
        cashUp.Variance = CashUpCalculator.ComputeVariance(declaredCash, expected);
        cashUp.CashierId = cashierId;
        cashUp.CashierSignedAtUtc = DateTime.UtcNow;
        cashUp.UpdatedAtUtc = DateTime.UtcNow;

        await _store.SaveLocalWriteAsync(cashUp);
        return cashUp;
    }

    /// <summary>Owner countersign after reviewing the variance.</summary>
    public async Task<CashUp> OwnerSignAsync(DateOnly tradingDate)
    {
        var cashUp = await GetCashUpAsync(tradingDate)
            ?? throw new InvalidOperationException("No cash-up for that day.");

        cashUp.OwnerSignedAtUtc = DateTime.UtcNow;
        cashUp.UpdatedAtUtc = DateTime.UtcNow;

        await _store.SaveLocalWriteAsync(cashUp);
        return cashUp;
    }

    public async Task<IReadOnlyList<CashMovement>> GetTradingDayMovementsAsync(DateOnly tradingDate)
    {
        var config = await GetConfigAsync();
        var (startUtc, endUtc) = TradingDay.GetUtcWindow(tradingDate, config.TradingDayRollHour);

        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.CashMovements.AsNoTracking()
            .Where(m => m.OccurredAtUtc >= startUtc && m.OccurredAtUtc < endUtc)
            .OrderBy(m => m.OccurredAtUtc)
            .ToListAsync();
    }

    private async Task<TenantConfig> GetConfigAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.TenantConfigs.AsNoTracking().FirstOrDefaultAsync() ?? new TenantConfig();
    }
}
