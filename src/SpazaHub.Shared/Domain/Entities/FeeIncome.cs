using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// Append-only service income event (cashback fees, VAS commission). Kept separate from
/// goods margin so daily reports show service income on its own line.
/// </summary>
public class FeeIncome : Entity, ITenantOwned, IAppendOnly
{
    public Guid TenantId { get; set; }

    public FeeIncomeType Type { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Id of the originating event, for example the CashbackTransaction.</summary>
    public Guid SourceId { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
