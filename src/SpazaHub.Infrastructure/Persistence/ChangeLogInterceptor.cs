using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SpazaHub.Application.Sync;
using SpazaHub.Domain.Common;
using SpazaHub.Shared.Sync;

namespace SpazaHub.Infrastructure.Persistence;

/// <summary>
/// Appends a TenantChangeLogEntry for every inserted or modified sync-registered entity,
/// no matter where the write originated (a device push or a server-side handler). This
/// single choke point guarantees all changes flow down to other devices via pull.
/// Runs after TenantStampInterceptor so entities already carry their final TenantId.
/// </summary>
public sealed class ChangeLogInterceptor : SaveChangesInterceptor
{
    private readonly ISyncDeviceContext _deviceContext;
    private readonly TimeProvider _clock;

    public ChangeLogInterceptor(ISyncDeviceContext deviceContext, TimeProvider clock)
    {
        _deviceContext = deviceContext;
        _clock = clock;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        AppendChangeLog(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AppendChangeLog(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AppendChangeLog(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        List<TenantChangeLogEntry>? newEntries = null;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (!SyncEntityRegistry.TryGet(entry.Entity.GetType(), out var descriptor))
            {
                continue;
            }

            var entity = (Entity)entry.Entity;
            var tenantId = ((ITenantOwned)entry.Entity).TenantId;

            (newEntries ??= []).Add(new TenantChangeLogEntry
            {
                TenantId = tenantId,
                EntityType = descriptor.Name,
                EntityId = entity.Id,
                PayloadJson = SyncJson.Serialize(entry.Entity, descriptor.ClrType),
                SourceDeviceId = _deviceContext.CurrentDeviceId,
                OccurredAtUtc = now
            });
        }

        if (newEntries is not null)
        {
            context.Set<TenantChangeLogEntry>().AddRange(newEntries);
        }
    }
}

/// <summary>Scoped holder for the device originating the current sync push.</summary>
public sealed class SyncDeviceContext : ISyncDeviceContext
{
    public Guid? CurrentDeviceId { get; set; }
}
