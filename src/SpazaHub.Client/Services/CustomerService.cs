using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Client.Services;

/// <summary>A customer with their derived book balance for list screens.</summary>
public sealed record CustomerWithBalance(Customer Customer, decimal Balance);

/// <summary>
/// The Makhulu Book: customers and their credit ledger, fully offline. Balances are
/// always derived from entries; payments received in cash go into the drawer stream
/// so cash-up still balances. Reminders are Tier 1: an sms: link from the owner's
/// own phone, recorded as a CustomerMessage for history.
/// </summary>
public class CustomerService
{
    private readonly LocalStore _store;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public CustomerService(LocalStore store, IDbContextFactory<ClientDbContext> contextFactory)
    {
        _store = store;
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<CustomerWithBalance>> ListWithBalancesAsync(bool includeInactive = false)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();

        var customers = await db.Customers.AsNoTracking()
            .Where(c => includeInactive || c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync();

        var entries = await db.CreditEntries.AsNoTracking().ToListAsync();
        var byCustomer = entries.GroupBy(e => e.CustomerId)
            .ToDictionary(g => g.Key, g => CreditLedger.Balance(g));

        return customers
            .Select(c => new CustomerWithBalance(c, byCustomer.GetValueOrDefault(c.Id)))
            .ToList();
    }

    /// <summary>POPIA: consent gets a capture timestamp the first time it is granted.</summary>
    public async Task SaveCustomerAsync(Customer customer)
    {
        customer.UpdatedAtUtc = DateTime.UtcNow;
        if (customer.CreatedAtUtc == default)
        {
            customer.CreatedAtUtc = customer.UpdatedAtUtc;
        }

        if (customer.ReminderConsent && customer.ConsentCapturedAtUtc is null)
        {
            customer.ConsentCapturedAtUtc = DateTime.UtcNow;
        }
        else if (!customer.ReminderConsent)
        {
            customer.ConsentCapturedAtUtc = null;
        }

        await _store.SaveLocalWriteAsync(customer);
    }

    public async Task<decimal> GetBalanceAsync(Guid customerId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var entries = await db.CreditEntries.AsNoTracking()
            .Where(e => e.CustomerId == customerId)
            .ToListAsync();
        return CreditLedger.Balance(entries);
    }

    public async Task<IReadOnlyList<CreditEntry>> GetLedgerAsync(Guid customerId, int take = 30)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.CreditEntries.AsNoTracking()
            .Where(e => e.CustomerId == customerId)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>Soft limit check before goods go on the book.</summary>
    public async Task<CreditLimitCheck> CheckLimitAsync(Guid customerId, decimal addAmount)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId);
        decimal balance = await GetBalanceAsync(customerId);
        return CreditLedger.CheckLimit(balance, addAmount, customer.CreditLimit);
    }

    /// <summary>Debit: goods taken on credit, linked to the sale when there is one.</summary>
    public async Task ChargeToBookAsync(
        Guid customerId, decimal amount, Guid? saleId = null, string? note = null, Guid? cashierId = null)
    {
        if (amount <= 0m)
        {
            throw new InvalidOperationException("Amount must be positive.");
        }

        await _store.SaveLocalWriteAsync(new CreditEntry
        {
            CustomerId = customerId,
            Type = CreditEntryType.Debit,
            Amount = amount,
            SaleId = saleId,
            Note = note,
            CashierId = cashierId,
            OccurredAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Credit: a payment against the book. Cash payments also enter the drawer stream
    /// so expected cash stays honest.
    /// </summary>
    public async Task ReceivePaymentAsync(
        Guid customerId, decimal amount, bool paidInCash = true, Guid? cashierId = null)
    {
        if (amount <= 0m)
        {
            throw new InvalidOperationException("Amount must be positive.");
        }

        await _store.SaveLocalWriteAsync(new CreditEntry
        {
            CustomerId = customerId,
            Type = CreditEntryType.Credit,
            Amount = amount,
            CashierId = cashierId,
            OccurredAtUtc = DateTime.UtcNow
        });

        if (paidInCash)
        {
            await _store.SaveLocalWriteAsync(new CashMovement
            {
                Type = CashMovementType.CreditPaymentReceived,
                Amount = amount,
                CashierId = cashierId,
                OccurredAtUtc = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Builds the sms: reminder link (owner's phone sends it, zero cost) and records
    /// the message for history. Returns null when the customer has no phone or has
    /// not consented: POPIA is not optional.
    /// </summary>
    public async Task<string?> BuildReminderLinkAsync(Guid customerId, string language = "en")
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId);

        if (string.IsNullOrWhiteSpace(customer.Phone) || !customer.ReminderConsent)
        {
            return null;
        }

        decimal balance = await GetBalanceAsync(customerId);
        if (balance <= 0m)
        {
            return null;
        }

        var state = await db.SyncState.AsNoTracking().FirstOrDefaultAsync();
        string shopName = string.IsNullOrWhiteSpace(state?.ShopName) ? "the shop" : state!.ShopName;

        string displayName = string.IsNullOrWhiteSpace(customer.Nickname) ? customer.Name : customer.Nickname;
        string body = ReminderSms.BuildBody(displayName, balance, shopName, language);

        await _store.SaveLocalWriteAsync(new CustomerMessage
        {
            CustomerId = customerId,
            TemplateKey = language.StartsWith("zu", StringComparison.OrdinalIgnoreCase) ? "reminder-zu" : "reminder-en",
            Body = body,
            Channel = MessageChannel.OwnerPhoneSms,
            Status = MessageStatus.Submitted,
            QueuedAtUtc = DateTime.UtcNow,
            SubmittedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        return ReminderSms.BuildLink(customer.Phone, body);
    }
}
