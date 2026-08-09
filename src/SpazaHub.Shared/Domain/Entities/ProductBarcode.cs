using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// A barcode that resolves to a <see cref="Product"/>. A product can have many: the plain
/// unit code, extra supplier/batch codes for the same item, a case code that sells or
/// receives several units at once, and scale-printed price-embedded codes. Mutable
/// reference data (last-write-wins on sync), like the product it points at.
/// </summary>
public class ProductBarcode : Entity, ITenantOwned, IMutableSynced
{
    public Guid TenantId { get; set; }

    public Guid ProductId { get; set; }

    /// <summary>
    /// For <see cref="BarcodeKind.Unit"/> and <see cref="BarcodeKind.Pack"/> this is the
    /// full scanned code. For <see cref="BarcodeKind.PriceEmbedded"/> it is only the
    /// item-reference digits: the price varies per label, so the whole scan is never equal.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    public BarcodeKind Kind { get; set; } = BarcodeKind.Unit;

    /// <summary>
    /// Units added to the cart (or received into stock) per scan. 1 for a single, the case
    /// size (e.g. 24) for a <see cref="BarcodeKind.Pack"/>. Ignored for price-embedded codes,
    /// which are always one item.
    /// </summary>
    public decimal UnitsPerScan { get; set; } = 1m;

    /// <summary>
    /// Soft-delete flag. The sync engine has no delete channel (rows only ever upsert), so a
    /// removed barcode is deactivated and syncs that change last-write-wins. Lookups ignore
    /// inactive codes.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
