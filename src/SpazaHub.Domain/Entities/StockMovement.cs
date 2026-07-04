using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// Append-only stock event. On-hand quantity is always SUM(Quantity) over movements;
/// it is derived, never synced.
/// </summary>
public class StockMovement : Entity, ITenantOwned, IAppendOnly
{
    public Guid TenantId { get; set; }

    public Guid ProductId { get; set; }

    public StockMovementType Type { get; set; }

    /// <summary>Signed quantity: negative for sales and wastage, positive for goods received.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Cost per unit, captured on every GoodsReceived movement.</summary>
    public decimal? UnitCost { get; set; }

    public Guid? SaleId { get; set; }

    public Guid? CashierId { get; set; }

    public string? Note { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
