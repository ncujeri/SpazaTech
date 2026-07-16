using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// One Makhulu Book ledger row. Debits are goods taken on credit (linked to a sale),
/// credits are payments received. Balance is derived by summation; append-only.
/// </summary>
public class CreditEntry : Entity, ITenantOwned, IAppendOnly
{
    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    public CreditEntryType Type { get; set; }

    /// <summary>Always positive; Type determines direction.</summary>
    public decimal Amount { get; set; }

    public Guid? SaleId { get; set; }

    public Guid? CashierId { get; set; }

    public string? Note { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
