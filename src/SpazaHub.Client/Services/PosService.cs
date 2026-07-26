using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Client.Services;

/// <summary>A product that dropped to or below its low-stock threshold during a sale.</summary>
public sealed record LowStockAlert(Guid ProductId, string Name, decimal QuantityLeft, decimal Threshold);

/// <summary>One original sale line with how much of it can still be brought back.</summary>
public sealed record ReturnableLine(
    Guid SaleLineId,
    Guid? ProductId,
    string Description,
    decimal SoldQuantity,
    decimal AlreadyReturned,
    decimal Remaining,
    decimal UnitPrice);

/// <summary>A cashier's request to bring back part of one sale line.</summary>
public sealed record ReturnRequest(Guid SaleLineId, decimal ReturnQuantity);

/// <summary>
/// Raised when a cash refund would take more cash than the drawer holds. The UI offers
/// the owner an override or a different refund method rather than letting the drawer go
/// negative silently.
/// </summary>
public sealed class DrawerShortException(decimal needed, decimal available)
    : InvalidOperationException(
        $"Refund needs R{needed:0.00} cash but the drawer holds R{available:0.00}.")
{
    public decimal Needed { get; } = needed;

    public decimal Available { get; } = available;
}

/// <summary>
/// Completes sales entirely locally: builds the immutable sale graph, writes every
/// entity through the outbox in parent-first order, updates cached quantities, and
/// reports low-stock hits. No network is touched on this path.
/// </summary>
public class PosService
{
    private readonly LocalStore _store;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;
    private readonly CashUpService _cashUp;

    public PosService(
        LocalStore store, IDbContextFactory<ClientDbContext> contextFactory, CashUpService cashUp)
    {
        _store = store;
        _contextFactory = contextFactory;
        _cashUp = cashUp;
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

    /// <summary>
    /// Rings up the cart on a customer's book: the whole sale tenders as StoreCredit
    /// and a linked debit lands in the Makhulu Book ledger. The credit limit warning
    /// happens in the UI before this is called; the limit is soft by design.
    /// </summary>
    public async Task<(CompletedSale Sale, IReadOnlyList<LowStockAlert> Alerts)> CompleteSaleOnBookAsync(
        IReadOnlyList<CartLine> cart, Guid customerId, Guid? cashierId = null)
    {
        var state = await _store.GetStateAsync()
            ?? throw new InvalidOperationException("Device not set up yet.");

        decimal total = cart.Sum(l => Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero));
        var completed = SaleBuilder.Build(
            cart,
            [new TenderInput(Domain.Enums.PaymentMethod.StoreCredit, total)],
            cashRoundingIncrement: 0m,
            state.DeviceId, cashierId, DateTime.UtcNow);

        foreach (var entity in completed.InWriteOrder())
        {
            await _store.SaveLocalWriteAsync(entity);
        }

        await _store.SaveLocalWriteAsync(new Domain.Entities.CreditEntry
        {
            CustomerId = customerId,
            Type = Domain.Enums.CreditEntryType.Debit,
            Amount = total,
            SaleId = completed.Sale.Id,
            CashierId = cashierId,
            OccurredAtUtc = DateTime.UtcNow
        });

        var alerts = await ApplyQuantityChangesAsync(completed);
        return (completed, alerts);
    }

    /// <summary>
    /// Voids a sale in full via a compensating sale referencing the original: every tender
    /// is refunded by its own method, stock returns to the shelf, and an on-book sale has
    /// its ledger debit cancelled. Cash refunds are guarded against an empty drawer.
    /// </summary>
    public async Task<CompletedSale> VoidSaleAsync(
        Guid saleId, Guid? cashierId = null, bool allowDrawerOverdraw = false)
    {
        var state = await _store.GetStateAsync()
            ?? throw new InvalidOperationException("Device not set up yet.");

        Sale original;
        List<SaleLine> lines;
        List<SalePayment> payments;
        CreditEntry? bookDebit;
        await using (var db = await _contextFactory.CreateDbContextAsync())
        {
            original = await db.Sales.AsNoTracking().FirstAsync(s => s.Id == saleId);

            bool alreadyReversed = await db.Sales.AnyAsync(s => s.ReversesSaleId == saleId);
            if (alreadyReversed)
            {
                throw new InvalidOperationException(
                    "This sale already has a return. Return the remaining items instead.");
            }

            lines = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == saleId).ToListAsync();
            payments = await db.SalePayments.AsNoTracking().Where(p => p.SaleId == saleId).ToListAsync();
            bookDebit = await db.CreditEntries.AsNoTracking()
                .FirstOrDefaultAsync(e => e.SaleId == saleId && e.Type == Domain.Enums.CreditEntryType.Debit);
        }

        var reversal = SaleBuilder.BuildFullReversal(
            original, lines, payments, state.DeviceId, cashierId, DateTime.UtcNow);

        await GuardDrawerAsync(reversal, allowDrawerOverdraw);

        foreach (var entity in reversal.InWriteOrder())
        {
            await _store.SaveLocalWriteAsync(entity);
        }

        // An on-book sale clears its debt: a compensating credit for the whole amount.
        if (bookDebit is not null)
        {
            await WriteBookCreditAsync(bookDebit.CustomerId, bookDebit.Amount, reversal.Sale.Id, cashierId);
        }

        await ApplyQuantityChangesAsync(reversal);
        return reversal;
    }

    /// <summary>
    /// Returns part of a sale: only the chosen lines and quantities come back, refunded by
    /// one method (cash, a book credit, or a card reversal). Repeatable up to each line's
    /// remaining quantity. Cash refunds are guarded against an empty drawer.
    /// </summary>
    public async Task<CompletedSale> ReturnItemsAsync(
        Guid saleId,
        IReadOnlyList<ReturnRequest> requests,
        PaymentMethod refundMethod,
        Guid? cashierId = null,
        bool allowDrawerOverdraw = false)
    {
        var state = await _store.GetStateAsync()
            ?? throw new InvalidOperationException("Device not set up yet.");

        decimal increment = await GetCashRoundingIncrementAsync();

        Sale original;
        List<SaleLine> lines;
        CreditEntry? bookDebit;
        await using (var db = await _contextFactory.CreateDbContextAsync())
        {
            original = await db.Sales.AsNoTracking().FirstAsync(s => s.Id == saleId);
            lines = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == saleId).ToListAsync();
            bookDebit = await db.CreditEntries.AsNoTracking()
                .FirstOrDefaultAsync(e => e.SaleId == saleId && e.Type == Domain.Enums.CreditEntryType.Debit);
        }

        if (refundMethod == PaymentMethod.StoreCredit && bookDebit is null)
        {
            throw new InvalidOperationException("This sale was not on the book, so it cannot be refunded to a book.");
        }

        var remaining = await GetRemainingByLineAsync(saleId, lines);

        var selections = new List<ReturnSelection>();
        foreach (var request in requests)
        {
            var line = lines.FirstOrDefault(l => l.Id == request.SaleLineId)
                ?? throw new InvalidOperationException("That line is not on this sale.");

            decimal remainingQty = remaining.GetValueOrDefault(line.Id, line.Quantity);
            if (request.ReturnQuantity <= 0m || request.ReturnQuantity > remainingQty)
            {
                throw new InvalidOperationException(
                    $"Only {remainingQty:0.###} of {line.Description} can still be returned.");
            }

            selections.Add(new ReturnSelection(line, request.ReturnQuantity));
        }

        var reversal = SaleBuilder.BuildPartialReversal(
            original, selections, refundMethod, increment, state.DeviceId, cashierId, DateTime.UtcNow);

        await GuardDrawerAsync(reversal, allowDrawerOverdraw);

        foreach (var entity in reversal.InWriteOrder())
        {
            await _store.SaveLocalWriteAsync(entity);
        }

        if (refundMethod == PaymentMethod.StoreCredit && bookDebit is not null)
        {
            decimal refundValue = selections.Sum(
                s => Math.Round(s.ReturnQuantity * s.OriginalLine.UnitPrice, 2, MidpointRounding.AwayFromZero));
            await WriteBookCreditAsync(bookDebit.CustomerId, refundValue, reversal.Sale.Id, cashierId);
        }

        await ApplyQuantityChangesAsync(reversal);
        return reversal;
    }

    /// <summary>The original lines of a sale with how much of each can still be returned.</summary>
    public async Task<IReadOnlyList<ReturnableLine>> GetReturnableLinesAsync(Guid saleId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var lines = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == saleId).ToListAsync();
        var remaining = await GetRemainingByLineAsync(saleId, lines);

        return lines.Select(l =>
        {
            decimal remainingQty = remaining.GetValueOrDefault(l.Id, l.Quantity);
            return new ReturnableLine(
                l.Id, l.ProductId, l.Description, l.Quantity,
                l.Quantity - remainingQty, remainingQty, l.UnitPrice);
        }).ToList();
    }

    /// <summary>The book customer a sale is linked to, or null when it was not on the book.</summary>
    public async Task<Guid?> GetBookCustomerIdAsync(Guid saleId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var debit = await db.CreditEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.SaleId == saleId && e.Type == Domain.Enums.CreditEntryType.Debit);
        return debit?.CustomerId;
    }

    /// <summary>Today's sales, newest first, for the recent-sales and returns screen.</summary>
    public async Task<IReadOnlyList<Sale>> GetRecentSalesAsync(int take = 30)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.Sales.AsNoTracking()
            .OrderByDescending(s => s.OccurredAtUtc)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>Remaining returnable quantity per original line, netting off prior returns.</summary>
    private async Task<Dictionary<Guid, decimal>> GetRemainingByLineAsync(Guid saleId, List<SaleLine> lines)
    {
        var lineIds = lines.Select(l => l.Id).ToList();

        await using var db = await _contextFactory.CreateDbContextAsync();
        var returnedRows = await db.SaleLines.AsNoTracking()
            .Where(l => l.ReversesSaleLineId != null && lineIds.Contains(l.ReversesSaleLineId.Value))
            .Select(l => new { LineId = l.ReversesSaleLineId!.Value, l.Quantity })
            .ToListAsync();

        // Reversal quantities are negative; a positive returned amount is their magnitude.
        var returnedByLine = returnedRows
            .GroupBy(r => r.LineId)
            .ToDictionary(g => g.Key, g => g.Sum(r => -r.Quantity));

        return lines.ToDictionary(l => l.Id, l => l.Quantity - returnedByLine.GetValueOrDefault(l.Id, 0m));
    }

    /// <summary>Blocks a cash refund that would overdraw the drawer unless explicitly allowed.</summary>
    private async Task GuardDrawerAsync(CompletedSale reversal, bool allowDrawerOverdraw)
    {
        if (allowDrawerOverdraw || reversal.CashMovement is null || reversal.CashMovement.Amount >= 0m)
        {
            return;
        }

        decimal needed = -reversal.CashMovement.Amount;
        decimal drawer = await _cashUp.GetDrawerCashAsync();
        if (needed > drawer)
        {
            throw new DrawerShortException(needed, drawer);
        }
    }

    private async Task WriteBookCreditAsync(Guid customerId, decimal amount, Guid saleId, Guid? cashierId)
    {
        if (amount <= 0m)
        {
            return;
        }

        await _store.SaveLocalWriteAsync(new CreditEntry
        {
            CustomerId = customerId,
            Type = Domain.Enums.CreditEntryType.Credit,
            Amount = amount,
            SaleId = saleId,
            CashierId = cashierId,
            Note = "Return",
            OccurredAtUtc = DateTime.UtcNow
        });
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
