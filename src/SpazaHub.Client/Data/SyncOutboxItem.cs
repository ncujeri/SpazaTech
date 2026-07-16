namespace SpazaHub.Client.Data;

/// <summary>
/// Client outbox row. Every local write appends one of these in the same transaction
/// as the entity write. A background service pushes ordered batches when online;
/// rows at or below the server's acked sequence are cleared.
/// </summary>
public class SyncOutboxItem
{
    public long Sequence { get; set; }

    public Guid EntityId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
