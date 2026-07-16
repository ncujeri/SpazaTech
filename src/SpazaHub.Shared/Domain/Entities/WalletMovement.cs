using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// Append-only movement on the tenant wallet: top-ups positive, VAS float draws and
/// SMS costs negative.
/// </summary>
public class WalletMovement : Entity, ITenantOwned, IAppendOnly
{
    public Guid TenantId { get; set; }

    /// <summary>Signed amount: positive top-up, negative spend.</summary>
    public decimal Amount { get; set; }

    /// <summary>Id of the originating event, e.g. a VasTransaction or CustomerMessage.</summary>
    public Guid? SourceId { get; set; }

    public string? Note { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
