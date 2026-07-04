using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Entities;

/// <summary>
/// Makhulu Book customer. Phone is optional by design: many credit customers are known
/// by name or nickname only. POPIA: consent flags are captured explicitly.
/// </summary>
public class Customer : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Nickname { get; set; }

    /// <summary>Optional phone in E.164 format. Never mandatory.</summary>
    public string? Phone { get; set; }

    public string? PhotoPath { get; set; }

    /// <summary>POPIA consent to receive payment reminders. Reply-STOP flips this off.</summary>
    public bool ReminderConsent { get; set; }

    public DateTime? ConsentCapturedAtUtc { get; set; }

    /// <summary>Per-customer credit limit, soft-enforced with cashier override warning.</summary>
    public decimal CreditLimit { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
