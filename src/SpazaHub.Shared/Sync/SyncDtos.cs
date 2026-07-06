namespace SpazaHub.Shared.Sync;

/// <summary>
/// One outbox row travelling to the server: an entity snapshot plus the per-device
/// monotonic sequence used for ordering and ack bookkeeping.
/// </summary>
public sealed record SyncItemDto(
    Guid EntityId,
    string EntityType,
    long DeviceSequence,
    DateTime ClientTimestampUtc,
    string PayloadJson);

/// <summary>Ordered batch of outbox items pushed by one device.</summary>
public sealed record SyncPushRequest(Guid DeviceId, IReadOnlyList<SyncItemDto> Items);

/// <summary>
/// Push acknowledgement. The client clears outbox rows with sequence less than or
/// equal to HighestAckedSequence. Replaying an acked batch (lost ack) is safe: items
/// dedupe on entity Id and the same ack is returned.
/// </summary>
public sealed record SyncPushResponse(
    long HighestAckedSequence,
    int AppliedCount,
    int DuplicateCount,
    int RejectedCount);

/// <summary>One change flowing down from the per-tenant change log.</summary>
public sealed record SyncChangeDto(
    long Cursor,
    string EntityType,
    Guid EntityId,
    string PayloadJson,
    DateTime ServerTimestampUtc);

/// <summary>
/// Page of changes since the client's cursor. When HasMore is true the client
/// immediately pulls again from NextCursor; the cursor is resumable at any point.
/// </summary>
public sealed record SyncPullResponse(
    IReadOnlyList<SyncChangeDto> Changes,
    long NextCursor,
    bool HasMore);
