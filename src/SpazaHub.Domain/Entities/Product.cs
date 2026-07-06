using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// Catalog item. Mutable reference data (last-write-wins on sync). Stock on hand is
/// derived from StockMovement rows; CachedQuantity is a local convenience only and is
/// never transmitted as truth.
/// </summary>
public class Product : Entity, ITenantOwned, IMutableSynced
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Barcode { get; set; }

    public decimal SellPrice { get; set; }

    /// <summary>
    /// Weighted average cost per unit, updated on every GoodsReceived movement.
    /// Snapshotted onto sale lines so historical margins never shift.
    /// </summary>
    public decimal WeightedAverageCost { get; set; }

    /// <summary>
    /// Denormalized on-hand quantity recomputed locally from stock movements for UI speed.
    /// Never synced as truth.
    /// </summary>
    public decimal CachedQuantity { get; set; }

    public decimal LowStockThreshold { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
