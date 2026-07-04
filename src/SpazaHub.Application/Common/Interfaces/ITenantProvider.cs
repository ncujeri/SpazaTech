namespace SpazaHub.Application.Common.Interfaces;

/// <summary>
/// Resolves the current tenant from the authenticated context (the JWT tenant_id claim
/// on the server). The client can never supply a TenantId; the server always stamps
/// from this provider.
/// </summary>
public interface ITenantProvider
{
    /// <summary>True when an authenticated tenant context is present.</summary>
    bool HasTenant { get; }

    /// <summary>The current tenant. Throws when no tenant context is present.</summary>
    Guid TenantId { get; }
}
