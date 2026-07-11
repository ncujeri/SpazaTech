using SpazaHub.Domain.Common;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// One tender on a sale. A sale supports a multi-tender breakdown, for example
/// part cash and part card.
/// </summary>
public class SalePayment : Entity, ITenantOwned, IAppendOnly
{
    public Guid TenantId { get; set; }

    public Guid SaleId { get; set; }

    public PaymentMethod Method { get; set; }

    public decimal Amount { get; set; }
}
