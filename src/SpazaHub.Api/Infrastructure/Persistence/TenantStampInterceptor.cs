using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SpazaHub.Api.Common.Interfaces;
using SpazaHub.Domain.Common;

namespace SpazaHub.Api.Persistence;

/// <summary>
/// Stamps TenantId from the authenticated context onto every inserted ITenantOwned row.
/// Any TenantId supplied by the client is overwritten: the client can never set or
/// spoof tenancy. Outside an authenticated tenant context (system flows such as
/// registration) the writer must set TenantId explicitly; an empty TenantId then fails
/// loudly instead of leaking rows into a null tenant.
/// </summary>
public sealed class TenantStampInterceptor : SaveChangesInterceptor
{
    private readonly ITenantProvider _tenantProvider;

    public TenantStampInterceptor(ITenantProvider tenantProvider)
    {
        _tenantProvider = tenantProvider;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        StampTenantId(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        StampTenantId(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void StampTenantId(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added)
            {
                if (_tenantProvider.HasTenant)
                {
                    entry.Entity.TenantId = _tenantProvider.TenantId;
                }
                else if (entry.Entity.TenantId == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        $"Cannot insert {entry.Metadata.ClrType.Name} without a tenant context or explicit TenantId.");
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                // Tenancy of an existing row can never change.
                entry.Property(nameof(ITenantOwned.TenantId)).IsModified = false;
            }
        }
    }
}
