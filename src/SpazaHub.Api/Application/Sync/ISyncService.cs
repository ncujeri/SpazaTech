using SpazaHub.Shared.Sync;

namespace SpazaHub.Api.Sync;

/// <summary>
/// Server-side sync engine port. Push is idempotent: replaying a batch whose ack was
/// lost dedupes on entity Id and returns the same acknowledgement. Pull walks the
/// per-tenant change log from a resumable cursor.
/// </summary>
public interface ISyncService
{
    Task<SyncPushResponse> PushAsync(
        Guid deviceId, IReadOnlyList<SyncItemDto> items, CancellationToken cancellationToken = default);

    Task<SyncPullResponse> PullAsync(
        long cursor, int pageSize, Guid? excludeDeviceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ambient source-device context for a sync push, consumed by the change log so pull
/// can exclude a device's own changes (echo suppression).
/// </summary>
public interface ISyncDeviceContext
{
    Guid? CurrentDeviceId { get; set; }
}
