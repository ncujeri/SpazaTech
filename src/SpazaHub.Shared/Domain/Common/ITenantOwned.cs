namespace SpazaHub.Domain.Common;

/// <summary>
/// Marks an entity as belonging to a single tenant. The server stamps TenantId from the
/// authenticated context on insert and applies a global query filter; clients can never
/// set or spoof it.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
