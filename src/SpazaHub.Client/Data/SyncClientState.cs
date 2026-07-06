namespace SpazaHub.Client.Data;

/// <summary>
/// Single-row local sync state: who this device is, the pull cursor, and the
/// monotonic outbox sequence counter.
/// </summary>
public class SyncClientState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public Guid TenantId { get; set; }

    public Guid DeviceId { get; set; }

    /// <summary>Highest change log cursor already applied locally.</summary>
    public long LastPullCursor { get; set; }

    /// <summary>Next outbox sequence to assign. Monotonic per device.</summary>
    public long NextSequence { get; set; } = 1;
}
