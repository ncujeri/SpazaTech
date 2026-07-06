using SpazaHub.Domain.Common;

namespace SpazaHub.Infrastructure.Persistence;

/// <summary>
/// Per-tenant change log with a monotonic cursor (the bigint identity Id). Every applied
/// write to a sync-registered entity appends here; clients pull "everything since
/// cursor X" to receive reference data and events from other devices.
/// </summary>
public class TenantChangeLogEntry : ITenantOwned
{
    /// <summary>Monotonic cursor value, database-generated.</summary>
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    /// <summary>Server-truth snapshot of the entity, serialized with SyncJson.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>Device that originated the change; null for server-originated writes.</summary>
    public Guid? SourceDeviceId { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
