using Microsoft.EntityFrameworkCore;
using SpazaHub.Domain.Entities;
using SpazaHub.Shared.Sync;

namespace SpazaHub.Sync.Tests;

public class SyncPullTests : IDisposable
{
    private readonly SyncTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task PushProductsAsync(Guid deviceId, int count, string prefix)
    {
        var items = new List<SyncItemDto>();
        for (int i = 0; i < count; i++)
        {
            var product = new Product
            {
                Name = $"{prefix} {i}",
                SellPrice = i + 1,
                UpdatedAtUtc = DateTime.UtcNow
            };
            items.Add(new SyncItemDto(product.Id, nameof(Product), i + 1, DateTime.UtcNow,
                SyncJson.Serialize(product, typeof(Product))));
        }

        await using var context = _harness.CreateContext();
        await _harness.CreateSyncService(context).PushAsync(deviceId, items);
    }

    [Fact]
    public async Task Pull_PagesThroughBacklogWithResumableCursor()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await PushProductsAsync(_harness.DeviceA1, 25, "Item");

        // The second device pulls in pages of 10.
        var received = new List<SyncChangeDto>();
        long cursor = 0;
        bool hasMore = true;
        int pages = 0;

        while (hasMore)
        {
            await using var context = _harness.CreateContext();
            var page = await _harness.CreateSyncService(context)
                .PullAsync(cursor, 10, _harness.DeviceA2);

            received.AddRange(page.Changes);
            cursor = page.NextCursor;
            hasMore = page.HasMore;
            pages++;
        }

        Assert.Equal(25, received.Count);
        Assert.Equal(3, pages);
        // Cursor values are strictly increasing: replays are impossible by construction.
        Assert.True(received.Zip(received.Skip(1)).All(p => p.First.Cursor < p.Second.Cursor));
    }

    [Fact]
    public async Task Pull_CursorReplay_ReturnsSamePageAgain()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await PushProductsAsync(_harness.DeviceA1, 5, "Item");

        await using var context = _harness.CreateContext();
        var sync = _harness.CreateSyncService(context);

        var first = await sync.PullAsync(0, 10, _harness.DeviceA2);
        // The device crashed before persisting its cursor and pulls from 0 again.
        var replay = await sync.PullAsync(0, 10, _harness.DeviceA2);

        Assert.Equal(first.Changes.Select(c => c.Cursor), replay.Changes.Select(c => c.Cursor));
        Assert.Equal(first.NextCursor, replay.NextCursor);
    }

    [Fact]
    public async Task Pull_ExcludesOwnDevicesChanges()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await PushProductsAsync(_harness.DeviceA1, 3, "From phone");
        await PushProductsAsync(_harness.DeviceA2, 2, "From tablet");

        await using var context = _harness.CreateContext();
        var sync = _harness.CreateSyncService(context);

        var phoneView = await sync.PullAsync(0, 100, _harness.DeviceA1);
        var tabletView = await sync.PullAsync(0, 100, _harness.DeviceA2);

        Assert.Equal(2, phoneView.Changes.Count);
        Assert.Equal(3, tabletView.Changes.Count);
    }

    [Fact]
    public async Task Pull_IncludesServerOriginatedChanges()
    {
        // A server-side write (no device context) must flow down to every device.
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await using (var context = _harness.CreateContext())
        {
            context.Cashiers.Add(new Cashier
            {
                Name = "Sipho",
                PinHash = "hash",
                UpdatedAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using var pullContext = _harness.CreateContext();
        var page = await _harness.CreateSyncService(pullContext).PullAsync(0, 100, _harness.DeviceA1);

        var change = Assert.Single(page.Changes);
        Assert.Equal(nameof(Cashier), change.EntityType);
        Assert.Contains("Sipho", change.PayloadJson);
        // The PIN hash syncs down so offline shift login works on other devices.
        Assert.Contains("hash", change.PayloadJson);
    }

    [Fact]
    public async Task Pull_IsTenantIsolated()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await PushProductsAsync(_harness.DeviceA1, 4, "Shop A item");

        _harness.TenantProvider.CurrentTenant = _harness.TenantB;
        await PushProductsAsync(_harness.DeviceB1, 1, "Shop B item");

        await using var context = _harness.CreateContext();
        var pageForB = await _harness.CreateSyncService(context).PullAsync(0, 100, null);

        var change = Assert.Single(pageForB.Changes);
        Assert.Contains("Shop B item", change.PayloadJson);
    }

    [Fact]
    public async Task Pull_PayloadsNeverContainTenantIdOrCachedQuantity()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;
        await PushProductsAsync(_harness.DeviceA1, 1, "Clean payload");

        await using var context = _harness.CreateContext();
        var page = await _harness.CreateSyncService(context).PullAsync(0, 100, null);

        var change = Assert.Single(page.Changes);
        Assert.DoesNotContain("tenantId", change.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cachedQuantity", change.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pull_EmptyChangeLog_ReturnsSameCursor()
    {
        _harness.TenantProvider.CurrentTenant = _harness.TenantA;

        await using var context = _harness.CreateContext();
        var page = await _harness.CreateSyncService(context).PullAsync(0, 100, null);

        Assert.Empty(page.Changes);
        Assert.Equal(0, page.NextCursor);
        Assert.False(page.HasMore);
    }
}
