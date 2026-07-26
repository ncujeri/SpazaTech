using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// One line on a sale. ProductId is null for "loose amount" rings of unbarcoded goods.
/// Immutable together with its parent sale.
/// </summary>
public class SaleLine : Entity, ITenantOwned, IAppendOnly
{
    public Guid TenantId { get; set; }

    public Guid SaleId { get; set; }

    public Guid? ProductId { get; set; }

    /// <summary>Display text, useful for loose amounts where there is no product.</summary>
    public string Description { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Weighted average cost at the moment of sale, snapshotted so historical margin
    /// reports never shift when costs change later.
    /// </summary>
    public decimal UnitCostSnapshot { get; set; }

    public decimal LineTotal { get; set; }

    /// <summary>
    /// Set on a reversal line to the original line it returns, so partial returns can
    /// track how much of each original line has already come back to the shelf.
    /// </summary>
    public Guid? ReversesSaleLineId { get; set; }
}
