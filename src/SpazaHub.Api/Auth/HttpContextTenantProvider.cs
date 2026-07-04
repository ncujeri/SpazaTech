using SpazaHub.Application.Common.Interfaces;
using SpazaHub.Shared.Auth;

namespace SpazaHub.Api.Auth;

/// <summary>
/// Resolves the tenant from the tenant_id claim of the authenticated JWT. This is the
/// only source of tenancy on the server; request bodies are never trusted for it.
/// </summary>
public class HttpContextTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private Guid? Resolve()
    {
        string? value = _httpContextAccessor.HttpContext?.User.FindFirst(AppClaimTypes.TenantId)?.Value;
        return Guid.TryParse(value, out Guid tenantId) && tenantId != Guid.Empty ? tenantId : null;
    }

    public bool HasTenant => Resolve() is not null;

    public Guid TenantId => Resolve()
        ?? throw new InvalidOperationException("No tenant context on this request.");
}
