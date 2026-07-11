namespace SpazaHub.Domain.Common;

/// <summary>
/// Marker for immutable, append-only event entities (sales, stock movements, ledger entries,
/// cash movements, and so on). Rows carrying this marker are never updated or deleted;
/// corrections are new compensating events referencing the original.
/// </summary>
public interface IAppendOnly
{
}
