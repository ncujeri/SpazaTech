using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// Prepaid float balance for the tenant, shared by VAS float and SMS credits.
/// Balance changes are recorded as WalletMovement rows; Balance is a cached sum.
/// </summary>
public class TenantWallet : Entity, ITenantOwned, IMutableSynced
{
    public Guid TenantId { get; set; }

    public decimal Balance { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
