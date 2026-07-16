using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SpazaHub.Api.Common.Exceptions;
using SpazaHub.Api.Common.Interfaces;
using SpazaHub.Api.Sync;
using SpazaHub.Domain.Common;
using SpazaHub.Domain.Entities;
using SpazaHub.Api.Persistence;
using SpazaHub.Shared.Sync;

namespace SpazaHub.Api.Sync;

/// <summary>
/// Server sync engine. Push applies an ordered device batch in one transaction:
/// append-only items dedupe on entity Id (idempotent replay), mutable items resolve
/// last-write-wins on UpdatedAtUtc with a conflict audit row for every decision that
/// discards data. Pull walks the per-tenant change log from a resumable cursor.
/// </summary>
public class SyncService : ISyncService
{
    private readonly AppDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ISyncDeviceContext _deviceContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        AppDbContext db,
        ITenantProvider tenantProvider,
        ISyncDeviceContext deviceContext,
        TimeProvider clock,
        ILogger<SyncService> logger)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _deviceContext = deviceContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SyncPushResponse> PushAsync(
        Guid deviceId, IReadOnlyList<SyncItemDto> items, CancellationToken cancellationToken = default)
    {
        Guid tenantId = _tenantProvider.TenantId;

        // The device must exist in the caller's tenant (global filter applies).
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && !d.IsRevoked, cancellationToken)
            ?? throw new AuthenticationFailedException("Unknown or revoked device for this shop.");

        _deviceContext.CurrentDeviceId = deviceId;

        int applied = 0;
        int duplicates = 0;
        int rejected = 0;
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        foreach (var item in items.OrderBy(i => i.DeviceSequence))
        {
            if (!SyncEntityRegistry.TryGet(item.EntityType, out var descriptor) || !descriptor.PushAllowed)
            {
                throw new AppValidationException(new Dictionary<string, string[]>
                {
                    ["items"] = [$"Entity type '{item.EntityType}' cannot be pushed."]
                });
            }

            if (SyncJson.Deserialize(item.PayloadJson, descriptor.ClrType) is not Entity entity)
            {
                throw new AppValidationException(new Dictionary<string, string[]>
                {
                    ["items"] = [$"Payload for {item.EntityType} {item.EntityId} is not valid."]
                });
            }

            entity.Id = item.EntityId;

            var existing = (Entity?)await _db.FindAsync(descriptor.ClrType, [item.EntityId], cancellationToken);

            if (existing is ITenantOwned owned && owned.TenantId != tenantId)
            {
                // Id collision with another tenant's row: never touch it, never confirm it exists.
                throw new ConflictException($"Cannot apply {item.EntityType} {item.EntityId}.");
            }

            if (existing is null)
            {
                _db.Add(entity);
                applied++;
                continue;
            }

            if (descriptor.Kind == SyncEntityKind.AppendOnly)
            {
                // Replay of an already-applied event (lost ack): idempotent no-op.
                duplicates++;
                continue;
            }

            var incoming = (IMutableSynced)entity;
            var current = (IMutableSynced)existing;

            if (incoming.UpdatedAtUtc > current.UpdatedAtUtc)
            {
                Audit(descriptor, item, incomingWon: true,
                    losingPayload: SyncJson.Serialize(existing, descriptor.ClrType),
                    incoming.UpdatedAtUtc, current.UpdatedAtUtc, deviceId, now);

                ApplyMutableUpdate(existing, entity);
                applied++;
            }
            else if (incoming.UpdatedAtUtc == current.UpdatedAtUtc)
            {
                // Replay of the same write (lost ack): idempotent no-op, not a conflict.
                duplicates++;
            }
            else
            {
                Audit(descriptor, item, incomingWon: false,
                    losingPayload: item.PayloadJson,
                    incoming.UpdatedAtUtc, current.UpdatedAtUtc, deviceId, now);
                rejected++;
            }
        }

        device.LastSeenAtUtc = now;
        long highestSequence = items.Max(i => i.DeviceSequence);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Sync push from device {DeviceId}: {Applied} applied, {Duplicates} duplicate, {Rejected} rejected, ack {Ack}.",
            deviceId, applied, duplicates, rejected, highestSequence);

        return new SyncPushResponse(highestSequence, applied, duplicates, rejected);
    }

    public async Task<SyncPullResponse> PullAsync(
        long cursor, int pageSize, Guid? excludeDeviceId, CancellationToken cancellationToken = default)
    {
        var query = _db.TenantChangeLog.Where(c => c.Id > cursor);

        if (excludeDeviceId is not null)
        {
            query = query.Where(c => c.SourceDeviceId == null || c.SourceDeviceId != excludeDeviceId);
        }

        var page = await query
            .OrderBy(c => c.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        bool hasMore = page.Count > pageSize;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        var changes = page
            .Select(c => new SyncChangeDto(c.Id, c.EntityType, c.EntityId, c.PayloadJson, c.OccurredAtUtc))
            .ToList();

        long nextCursor = page.Count > 0 ? page[^1].Id : cursor;

        return new SyncPullResponse(changes, nextCursor, hasMore);
    }

    /// <summary>
    /// Copies incoming values onto the tracked row while preserving server-owned fields:
    /// TenantId is pinned by TenantStampInterceptor and CachedQuantity is never taken
    /// from a payload (SyncJson excludes it, so the deserialized value is a default).
    /// </summary>
    private void ApplyMutableUpdate(Entity existing, Entity incoming)
    {
        decimal? preservedCachedQuantity = (existing as Product)?.CachedQuantity;
        Guid preservedTenantId = ((ITenantOwned)existing).TenantId;

        _db.Entry(existing).CurrentValues.SetValues(incoming);

        ((ITenantOwned)existing).TenantId = preservedTenantId;
        if (existing is Product product && preservedCachedQuantity is not null)
        {
            product.CachedQuantity = preservedCachedQuantity.Value;
        }
    }

    private void Audit(
        SyncEntityDescriptor descriptor, SyncItemDto item, bool incomingWon, string losingPayload,
        DateTime incomingUpdatedAt, DateTime existingUpdatedAt, Guid deviceId, DateTime now)
    {
        _db.SyncConflictAudits.Add(new SyncConflictAudit
        {
            EntityType = descriptor.Name,
            EntityId = item.EntityId,
            IncomingWon = incomingWon,
            LosingPayloadJson = losingPayload,
            IncomingUpdatedAtUtc = incomingUpdatedAt,
            ExistingUpdatedAtUtc = existingUpdatedAt,
            SourceDeviceId = deviceId,
            OccurredAtUtc = now
        });
    }
}
