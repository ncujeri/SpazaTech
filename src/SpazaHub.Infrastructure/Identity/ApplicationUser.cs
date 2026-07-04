using Microsoft.AspNetCore.Identity;

namespace SpazaHub.Infrastructure.Identity;

/// <summary>
/// Identity account for a shop owner. Username is the E.164 phone number. Cashiers are
/// not Identity users: they are Cashier rows unlocked by PIN on a registered device.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public Guid TenantId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
