using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// A sub-user of the shop who rings up sales. Logs in on a registered device with a
/// 4-digit PIN. Never sees cost prices, margins, or reports.
/// </summary>
public class Cashier : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash of the 4-digit PIN. The raw PIN is never stored.</summary>
    public string PinHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Role flag: whether this cashier may perform till cashback.</summary>
    public bool CanDoCashback { get; set; }

    /// <summary>Consecutive failed PIN attempts; lockout applies past the limit.</summary>
    public int FailedPinAttempts { get; set; }

    public DateTime? LockedOutUntilUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
