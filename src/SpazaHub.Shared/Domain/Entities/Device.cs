using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// A physical phone or tablet registered to the shop. Multi-device per tenant is a
/// day-one assumption (owner phone plus counter tablet). Cashier PIN login is only
/// valid on a registered device holding a live device refresh token.
/// </summary>
public class Device : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime RegisteredAtUtc { get; set; }

    public DateTime? LastSeenAtUtc { get; set; }

    public bool IsRevoked { get; set; }
}
