using SpazaHub.Domain.Common;

namespace SpazaHub.Infrastructure.Persistence;

/// <summary>
/// Server-side audit of every last-write-wins conflict resolution. When an incoming
/// edit wins, the overwritten server values are preserved here; when it loses, the
/// rejected incoming payload is preserved. Nothing is silently discarded.
/// </summary>
public class SyncConflictAudit : ITenantOwned
{
    public Guid Id { get; set; } = GuidV7.NewGuid();

    public Guid TenantId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    /// <summary>True when the incoming write overwrote the server row.</summary>
    public bool IncomingWon { get; set; }

    /// <summary>The payload that lost: prior server values if the incoming won, else the rejected incoming payload.</summary>
    public string LosingPayloadJson { get; set; } = string.Empty;

    public DateTime IncomingUpdatedAtUtc { get; set; }

    public DateTime ExistingUpdatedAtUtc { get; set; }

    public Guid? SourceDeviceId { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
