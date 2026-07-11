using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// Append-only record of physical cash entering or leaving the drawer. Cash-up expected
/// cash is derived from these rows for the trading day.
/// </summary>
public class CashMovement : Entity, ITenantOwned, IAppendOnly
{
    public Guid TenantId { get; set; }

    public CashMovementType Type { get; set; }

    /// <summary>Signed: positive into the drawer, negative out of the drawer.</summary>
    public decimal Amount { get; set; }

    public Guid? SaleId { get; set; }

    public Guid? CashbackTransactionId { get; set; }

    public Guid? CashierId { get; set; }

    public string? Note { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
