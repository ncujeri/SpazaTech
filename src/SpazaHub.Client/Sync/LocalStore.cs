using Microsoft.EntityFrameworkCore;
using SpazaHub.Client.Data;
using SpazaHub.Domain.Common;
using SpazaHub.Domain.Entities;
using SpazaHub.Shared.Sync;

namespace SpazaHub.Client.Sync;

/// <summary>
/// All local database writes go through here. Local writes append an outbox row in the
/// same transaction as the entity; remote changes from pull are applied without
/// touching the outbox (no echo). Product.CachedQuantity is recomputed locally from
/// stock movements, never taken from the wire.
/// </summary>
public class LocalStore
{
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;
    private readonly OpfsDbPersistence _persistence;
    private readonly ILogger<LocalStore> _logger;
    private bool _schemaEnsured;

    public LocalStore(
        IDbContextFactory<ClientDbContext> contextFactory,
        OpfsDbPersistence persistence,
        ILogger<LocalStore> logger)
    {
        _contextFactory = contextFactory;
        _persistence = persistence;
        _logger = logger;
    }

    /// <summary>
    /// Creates the schema on first touch. A fresh device queries state before setup;
    /// that must return empty, not crash on a missing table.
    /// </summary>
    private async Task EnsureSchemaAsync(ClientDbContext db)
    {
        if (!_schemaEnsured)
        {
            await db.Database.EnsureCreatedAsync();

            // Microsoft.Data.Sqlite defaults new databases to WAL, which leaves the
            // data in a -wal side file. OPFS persistence copies the main file, so
            // fold everything into it and stay on the rollback journal.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
            _schemaEnsured = true;
        }
    }

    /// <summary>Creates the schema and the sync state row after login on a new device.</summary>
    public async Task InitializeAsync(Guid tenantId, Guid deviceId, string? shopName = null)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var state = await db.SyncState.FindAsync(SyncClientState.SingletonId);
        if (state is null)
        {
            db.SyncState.Add(new SyncClientState
            {
                TenantId = tenantId,
                DeviceId = deviceId,
                ShopName = shopName ?? string.Empty
            });
            await db.SaveChangesAsync();
        }

        await _persistence.SaveAsync();
    }

    public async Task<SyncClientState?> GetStateAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        return await db.SyncState.AsNoTracking().FirstOrDefaultAsync();
    }

    /// <summary>
    /// Upserts an entity locally and appends the outbox row in one transaction.
    /// The entity must be registered for sync and tenant-owned.
    /// </summary>
    public async Task SaveLocalWriteAsync(Entity entity)
    {
        Type entityType = entity.GetType();

        if (entity is not ITenantOwned owned)
        {
            throw new InvalidOperationException($"{entityType.Name} is not tenant-owned.");
        }

        if (!SyncEntityRegistry.TryGet(entityType, out var descriptor) || !descriptor.PushAllowed)
        {
            throw new InvalidOperationException($"{entityType.Name} is not a pushable sync entity.");
        }

        await using var db = await _contextFactory.CreateDbContextAsync();

        var state = await db.SyncState.FirstAsync();
        owned.TenantId = state.TenantId;

        var existing = await db.FindAsync(entityType, entity.Id);
        if (existing is null)
        {
            db.Add(entity);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(entity);
        }

        db.SyncOutbox.Add(new SyncOutboxItem
        {
            Sequence = state.NextSequence++,
            EntityId = entity.Id,
            EntityType = descriptor.Name,
            PayloadJson = SyncJson.Serialize(entity, descriptor.ClrType),
            CreatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        await _persistence.SaveAsync();
    }

    /// <summary>Oldest pending outbox rows, ordered by sequence, as wire items.</summary>
    public async Task<IReadOnlyList<SyncItemDto>> GetPendingItemsAsync(int max)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var rows = await db.SyncOutbox.AsNoTracking()
            .OrderBy(o => o.Sequence)
            .Take(max)
            .ToListAsync();

        return rows
            .Select(o => new SyncItemDto(o.EntityId, o.EntityType, o.Sequence, o.CreatedAtUtc, o.PayloadJson))
            .ToList();
    }

    /// <summary>Clears outbox rows covered by the server's acknowledgement.</summary>
    public async Task ClearAckedAsync(long highestAckedSequence)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var acked = await db.SyncOutbox
            .Where(o => o.Sequence <= highestAckedSequence)
            .ToListAsync();

        db.SyncOutbox.RemoveRange(acked);
        await db.SaveChangesAsync();
        await _persistence.SaveAsync();
    }

    /// <summary>
    /// Applies one page of pulled changes: upsert by Id, client-side LWW guard for
    /// mutable rows with newer local edits, cursor advance, and cached quantity
    /// recompute for affected products.
    /// </summary>
    public async Task ApplyRemoteChangesAsync(IReadOnlyList<SyncChangeDto> changes, long nextCursor)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await using var db = await _contextFactory.CreateDbContextAsync();
        var state = await db.SyncState.FirstAsync();
        var productsToRecompute = new HashSet<Guid>();

        foreach (var change in changes)
        {
            if (!SyncEntityRegistry.TryGet(change.EntityType, out var descriptor))
            {
                _logger.LogWarning("Skipping unknown entity type {Type} from pull.", change.EntityType);
                continue;
            }

            if (SyncJson.Deserialize(change.PayloadJson, descriptor.ClrType) is not Entity entity)
            {
                _logger.LogWarning("Skipping malformed payload for {Type} {Id}.", change.EntityType, change.EntityId);
                continue;
            }

            entity.Id = change.EntityId;
            ((ITenantOwned)entity).TenantId = state.TenantId;

            var existing = await db.FindAsync(descriptor.ClrType, change.EntityId);

            if (existing is null)
            {
                db.Add(entity);
            }
            else if (descriptor.Kind == SyncEntityKind.MutableLastWriteWins)
            {
                // Keep a newer local edit; it is still in the outbox and will win server-side.
                if (((IMutableSynced)existing).UpdatedAtUtc >= ((IMutableSynced)entity).UpdatedAtUtc)
                {
                    continue;
                }

                decimal? cachedQuantity = (existing as Product)?.CachedQuantity;
                db.Entry(existing).CurrentValues.SetValues(entity);
                ((ITenantOwned)existing).TenantId = state.TenantId;
                if (existing is Product product && cachedQuantity is not null)
                {
                    product.CachedQuantity = cachedQuantity.Value;
                }
            }
            // Append-only rows already present are replays: nothing to do.

            if (entity is StockMovement movement)
            {
                productsToRecompute.Add(movement.ProductId);
            }
        }

        state.LastPullCursor = nextCursor;
        await db.SaveChangesAsync();

        await RecomputeCachedQuantitiesAsync(db, productsToRecompute);
        await _persistence.SaveAsync();
    }

    private static async Task RecomputeCachedQuantitiesAsync(ClientDbContext db, HashSet<Guid> productIds)
    {
        if (productIds.Count == 0)
        {
            return;
        }

        foreach (Guid productId in productIds)
        {
            var product = await db.Products.FindAsync(productId);
            if (product is null)
            {
                continue;
            }

            // SQLite cannot aggregate decimals server-side; sum the quantities in memory.
            var quantities = await db.StockMovements
                .Where(m => m.ProductId == productId)
                .Select(m => m.Quantity)
                .ToListAsync();
            product.CachedQuantity = quantities.Sum();
        }

        await db.SaveChangesAsync();
    }
}
