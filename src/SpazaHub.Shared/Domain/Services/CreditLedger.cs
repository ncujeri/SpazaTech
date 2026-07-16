using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Services;

/// <summary>Outcome of a credit limit check before putting goods on the book.</summary>
public enum CreditLimitCheck
{
    Ok = 0,

    /// <summary>Over the limit; cashier may proceed only if the owner allows overrides.</summary>
    OverLimit = 1
}

/// <summary>
/// Makhulu Book arithmetic. The book is a ledger, never a balance field: what a
/// customer owes is always the sum of debits minus credits, so two devices can add
/// entries offline and the balance stays right after sync.
/// </summary>
public static class CreditLedger
{
    /// <summary>Amount currently owed. Positive means the customer owes the shop.</summary>
    public static decimal Balance(IEnumerable<CreditEntry> entries)
        => entries.Sum(e => e.Type == CreditEntryType.Debit ? e.Amount : -e.Amount);

    /// <summary>
    /// Soft credit limit check. A limit of zero means no limit is set. The limit warns,
    /// it never hard-blocks: whether a cashier may override is owner configuration.
    /// </summary>
    public static CreditLimitCheck CheckLimit(decimal currentBalance, decimal addAmount, decimal creditLimit)
    {
        if (creditLimit <= 0m)
        {
            return CreditLimitCheck.Ok;
        }

        return currentBalance + addAmount > creditLimit
            ? CreditLimitCheck.OverLimit
            : CreditLimitCheck.Ok;
    }
}
