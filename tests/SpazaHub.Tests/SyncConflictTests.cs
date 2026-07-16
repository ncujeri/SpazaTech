using Microsoft.EntityFrameworkCore;
using SpazaHub.Domain.Entities;
using SpazaHub.Shared.Sync;

namespace SpazaHub.Sync.Tests;

public class SyncConflictTests : IDisposable
{
    private readonly SyncTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<Product> SeedProductAsync(string name, decimal price, DateTime updatedAt)
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using var context = _harness.CreateContext();
        var product = new Product
        {
            Name = name,
            SellPrice = price,
            CachedQuantity = 42m,
            UpdatedAtUtc = updatedAt
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product;
    }

    private static SyncItemDto ProductEdit(Product product, long sequence)
        => new(product.Id, nameof(Product), sequence, DateTime.UtcNow,
            SyncJson.Serialize(product, typeof(Product)));

    [Fact]
    public async Task Push_NewerEdit_WinsAndAuditsOverwrittenValues()
    {
        var baseTime = DateTime.UtcNow.AddHours(-2);
        var seeded = await SeedProductAsync("Bread", 18m, baseTime);

        var edit = new Product
        {
            Id = seeded.Id,
            Name = "Bread white 700g",
            SellPrice = 19m,
            UpdatedAtUtc = baseTime.AddHours(1)
        };

        await using (var context = _harness.CreateContext())
        {
            var response = await _harness.CreateSyncService(context)
                .PushAsync(_harness.DeviceA1, [ProductEdit(edit, 1)]);
            Assert.Equal(1, response.AppliedCount);
            Assert.Equal(0, response.RejectedCount);
        }

        await using (var verify = _harness.CreateContext())
        {
            var product = await verify.Products.SingleAsync();
            Assert.Equal("Bread white 700g", product.Name);
            Assert.Equal(19m, product.SellPrice);

            var audit = await verify.SyncConflictAudits.SingleAsync();
            Assert.True(audit.IncomingWon);
            Assert.Contains("18", audit.LosingPayloadJson);
            Assert.Contains("Bread", audit.LosingPayloadJson);
        }
    }

    [Fact]
    public async Task Push_StaleEdit_LosesAndIsAudited()
    {
        var baseTime = DateTime.UtcNow;
        var seeded = await SeedProductAsync("Milk 1L", 22m, baseTime);

        var staleEdit = new Product
        {
            Id = seeded.Id,
            Name = "Milk old name",
            SellPrice = 20m,
            UpdatedAtUtc = baseTime.AddHours(-3)
        };

        await using (var context = _harness.CreateContext())
        {
            var response = await _harness.CreateSyncService(context)
                .PushAsync(_harness.DeviceA1, [ProductEdit(staleEdit, 1)]);
            Assert.Equal(0, response.AppliedCount);
            Assert.Equal(1, response.RejectedCount);
        }

        await using (var verify = _harness.CreateContext())
        {
            var product = await verify.Products.SingleAsync();
            Assert.Equal("Milk 1L", product.Name);
            Assert.Equal(22m, product.SellPrice);

            var audit = await verify.SyncConflictAudits.SingleAsync();
            Assert.False(audit.IncomingWon);
            Assert.Contains("Milk old name", audit.LosingPayloadJson);
        }
    }

    [Fact]
    public async Task Push_ConflictingPriceEditsFromTwoDevices_LastWriteWins()
    {
        var baseTime = DateTime.UtcNow.AddHours(-5);
        var seeded = await SeedProductAsync("Sugar 2kg", 40m, baseTime);

        var editFromTablet = new Product
        {
            Id = seeded.Id, Name = "Sugar 2kg", SellPrice = 42m, UpdatedAtUtc = baseTime.AddHours(1)
        };
        var editFromPhone = new Product
        {
            Id = seeded.Id, Name = "Sugar 2kg", SellPrice = 45m, UpdatedAtUtc = baseTime.AddHours(2)
        };

        // The tablet syncs first with the older edit; the phone follows with the newer.
        await using (var context = _harness.CreateContext())
        {
            await _harness.CreateSyncService(context).PushAsync(_harness.DeviceA2, [ProductEdit(editFromTablet, 1)]);
        }

        await using (var context = _harness.CreateContext())
        {
            await _harness.CreateSyncService(context).PushAsync(_harness.DeviceA1, [ProductEdit(editFromPhone, 1)]);
        }

        await using (var verify = _harness.CreateContext())
        {
            Assert.Equal(45m, (await verify.Products.SingleAsync()).SellPrice);
            // Two LWW decisions were audited: seed overwritten by tablet, tablet by phone.
            Assert.Equal(2, await verify.SyncConflictAudits.CountAsync(a => a.IncomingWon));
        }
    }

    [Fact]
    public async Task Push_ReplayedMutableItem_IsDuplicateNotConflict()
    {
        var timestamp = DateTime.UtcNow;
        var seeded = await SeedProductAsync("Rice 2kg", 35m, timestamp);

        // The exact same write arrives again after a lost ack.
        var replay = new Product
        {
            Id = seeded.Id, Name = "Rice 2kg", SellPrice = 35m, UpdatedAtUtc = timestamp
        };

        await using (var context = _harness.CreateContext())
        {
            var response = await _harness.CreateSyncService(context)
                .PushAsync(_harness.DeviceA1, [ProductEdit(replay, 1)]);

            Assert.Equal(1, response.DuplicateCount);
            Assert.Equal(0, response.RejectedCount);
        }

        await using (var verify = _harness.CreateContext())
        {
            Assert.Equal(0, await verify.SyncConflictAudits.CountAsync());
        }
    }

    [Fact]
    public async Task Push_CachedQuantityInPayload_IsNeverApplied()
    {
        var seeded = await SeedProductAsync("Maize meal", 55m, DateTime.UtcNow.AddHours(-1));

        var edit = new Product
        {
            Id = seeded.Id,
            Name = "Maize meal 5kg",
            SellPrice = 56m,
            UpdatedAtUtc = DateTime.UtcNow
        };

        // Smuggle a cachedQuantity into the payload; the contract must drop it.
        string payload = SyncJson.Serialize(edit, typeof(Product))
            .TrimEnd('}') + ",\"cachedQuantity\":999999}";
        var item = new SyncItemDto(edit.Id, nameof(Product), 1, DateTime.UtcNow, payload);

        await using (var context = _harness.CreateContext())
        {
            await _harness.CreateSyncService(context).PushAsync(_harness.DeviceA1, [item]);
        }

        await using (var verify = _harness.CreateContext())
        {
            var product = await verify.Products.SingleAsync();
            Assert.Equal("Maize meal 5kg", product.Name);
            Assert.Equal(42m, product.CachedQuantity);
        }
    }
}
