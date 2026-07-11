using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// A tenant is one spaza shop business. All tenant-owned data hangs off this via TenantId.
/// Not itself tenant-owned: it is the root the filter resolves against.
/// </summary>
public class Tenant : Entity
{
    public string ShopName { get; set; } = string.Empty;

    /// <summary>Owner's phone number in E.164 format, e.g. +27821234567. Used for OTP login.</summary>
    public string OwnerPhone { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public bool IsActive { get; set; } = true;
}
