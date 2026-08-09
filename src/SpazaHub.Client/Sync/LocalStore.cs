using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
    /// Creates the schema on first touch and reconciles it with the current model. A
    /// fresh device queries state before setup; that must return empty, not crash on a
    /// missing table. An updated device may carry a database built by an older app
    /// version, so we also add tables and columns that appeared since it was created.
    /// </summary>
    private async Task EnsureSchemaAsync(ClientDbContext db)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await db.Database.EnsureCreatedAsync();

        // EnsureCreated is a no-op once the file exists: it never touches an existing
        // database, so a schema added by a newer app version would be missing. Bring an
        // older on-device database up to date without losing its rows.
        await ReconcileSchemaAsync(db);

        // Microsoft.Data.Sqlite defaults new databases to WAL, which leaves the
        // data in a -wal side file. OPFS persistence copies the main file, so
        // fold everything into it and stay on the rollback journal.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
        _schemaEnsured = true;
    }

    /// <summary>
    /// Aligns the on-device SQLite schema with the current EF model. Idempotent: it
    /// creates any missing tables and indexes (IF NOT EXISTS) and adds any missing
    /// columns to tables that already exist. On a freshly created database everything is
    /// already present, so this does nothing.
    /// </summary>
    private static async Task ReconcileSchemaAsync(ClientDbContext db)
    {
        // Create tables/indexes the model expects but an older database lacks. EF emits
        // plain CREATE statements; making them IF NOT EXISTS lets us run the whole script
        // over an existing database without clashing on the objects it already has.
        var createScript = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE \"", "CREATE TABLE IF NOT EXISTS \"")
            .Replace("CREATE UNIQUE INDEX \"", "CREATE UNIQUE INDEX IF NOT EXISTS \"")
            .Replace("CREATE INDEX \"", "CREATE INDEX IF NOT EXISTS \"");
        await db.Database.ExecuteSqlRawAsync(createScript);

        // Add columns that appeared on tables the database already had. CREATE TABLE IF
        // NOT EXISTS leaves an existing table untouched, so new columns need an ALTER.
        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrEmpty(tableName))
            {
                continue;
            }

            var existingColumns = await GetColumnsAsync(db, tableName);
            if (existingColumns.Count == 0)
            {
                // Table did not exist before this run; the create script just built it in
                // full, so there is nothing to reconcile.
                continue;
            }

            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName(storeObject);
                if (string.IsNullOrEmpty(columnName) || existingColumns.Contains(columnName))
                {
                    continue;
                }

                var columnType = property.GetColumnType(storeObject);
                var nullable = property.IsColumnNullable(storeObject);
                await db.Database.ExecuteSqlRawAsync(BuildAddColumnSql(tableName, columnName, columnType, nullable));
            }
        }
    }

    /// <summary>Reads the column names of a table, or an empty set if it does not exist.</summary>
    private static async Task<HashSet<string>> GetColumnsAsync(ClientDbContext db, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        // PRAGMA table_info returns one row per column (name is column index 1) and an
        // empty result for a table that does not exist — no error to catch.
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string BuildAddColumnSql(string table, string column, string columnType, bool nullable)
    {
        var sql = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {columnType}";
        if (!nullable)
        {
            // SQLite requires a default when adding a NOT NULL column to a table that may
            // already hold rows. Seed the type's zero value; synced data overwrites it.
            sql += $" NOT NULL DEFAULT {ZeroLiteralFor(columnType)}";
        }

        return sql;
    }

    private static string ZeroLiteralFor(string columnType)
    {
        var type = columnType.ToUpperInvariant();
        if (type.Contains("INT") || type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB"))
        {
            return "0";
        }

        return type.Contains("BLOB") ? "x''" : "''";
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

    /// <summary>
    /// Drops a single pending outbox row by sequence without touching the entity it
    /// snapshotted. Used to abandon a change that repeatedly fails to sync: the local data
    /// stays, but that write will never reach the server or other devices. Owner-only,
    /// guarded by a confirmation in the UI. Removing a middle row is safe — the server
    /// acks by highest sequence and remaining rows still push in order.
    /// </summary>
    public async Task DiscardPendingAsync(long sequence)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var row = await db.SyncOutbox.FirstOrDefaultAsync(o => o.Sequence == sequence);
        if (row is null)
        {
            return;
        }

        db.SyncOutbox.Remove(row);
        await db.SaveChangesAsync();
        await _persistence.SaveAsync();
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
