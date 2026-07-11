namespace SpazaHub.Domain.Common;

/// <summary>
/// Marks mutable reference data that syncs with last-write-wins conflict resolution
/// (product details, prices, thresholds, customer info, tenant config). UpdatedAtUtc is
/// the LWW timestamp; the sync engine audits overwritten values server-side.
/// Append-only event data never carries this: it cannot conflict by construction.
/// </summary>
public interface IMutableSynced
{
    DateTime UpdatedAtUtc { get; set; }
}
