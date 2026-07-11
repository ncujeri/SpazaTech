using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;

namespace SpazaHub.Sync.Tests;

/// <summary>
/// Full protocol round trip using the real client LocalStore on both ends and the real
/// server SyncService in the middle, exactly as the HTTP endpoints would drive them.
/// </summary>
public class ClientSyncRoundTripTests : IDisposable
{
    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new NotSupportedException("No JS in tests.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException("No JS in tests.");
    }

    private sealed class TestContextFactory : IDbContextFactory<ClientDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;

        public TestContextFactory()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
        }

        public ClientDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<ClientDbContext>().UseSqlite(_connection).Options);

        public void Dispose() => _connection.Dispose();
    }

    private readonly SyncTestHarness _server = new();
    private readonly TestContextFactory _factoryA = new();
    private readonly TestContextFactory _factoryB = new();
    private readonly LocalStore _clientA;
    private readonly LocalStore _clientB;

    public ClientSyncRoundTripTests()
    {
        var persistence = new OpfsDbPersistence(new StubJsRuntime(), NullLogger<OpfsDbPersistence>.Instance);
        _clientA = new LocalStore(_factoryA, persistence, NullLogger<LocalStore>.Instance);
        _clientB = new LocalStore(_factoryB, persistence, NullLogger<LocalStore>.Instance);
    }

    public void Dispose()
    {
        _server.Dispose();
        _factoryA.Dispose();
        _factoryB.Dispose();
    }

    [Fact]
    public async Task ProductAndStock_FlowFromDeviceAToDeviceB_WithDerivedQuantity()
    {
        await _clientA.InitializeAsync(_server.TenantA, _server.DeviceA1);
        await _clientB.InitializeAsync(_server.TenantA, _server.DeviceA2);

        // Device A rings up new reference data and a goods-received event offline.
        var product = new Product
        {
            Name = "Cooldrink 330ml",
            SellPrice = 12m,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await _clientA.SaveLocalWriteAsync(product);
        await _clientA.SaveLocalWriteAsync(new StockMovement
        {
            ProductId = product.Id,
            Type = StockMovementType.GoodsReceived,
            Quantity = 24m,
            UnitCost = 8m,
            OccurredAtUtc = DateTime.UtcNow
        });
        await _clientA.SaveLocalWriteAsync(new StockMovement
        {
            ProductId = product.Id,
            Type = StockMovementType.Sale,
            Quantity = -2m,
            OccurredAtUtc = DateTime.UtcNow
        });

        // Device A comes online: push the outbox, clear on ack.
        _server.TenantProvider.CurrentTenant = _server.TenantA;
        var pending = await _clientA.GetPendingItemsAsync(200);
        Assert.Equal(3, pending.Count);

        await using (var context = _server.CreateContext())
        {
            var response = await _server.CreateSyncService(context)
                .PushAsync(_server.DeviceA1, pending);
            Assert.Equal(3, response.AppliedCount);
            await _clientA.ClearAckedAsync(response.HighestAckedSequence);
        }

        Assert.Empty(await _clientA.GetPendingItemsAsync(200));

        // Device B pulls and applies.
        var stateB = await _clientB.GetStateAsync();
        await using (var context = _server.CreateContext())
        {
            var page = await _server.CreateSyncService(context)
                .PullAsync(stateB!.LastPullCursor, 200, _server.DeviceA2);
            Assert.Equal(3, page.Changes.Count);
            await _clientB.ApplyRemoteChangesAsync(page.Changes, page.NextCursor);
        }

        // Device B now has the product with quantity derived from movements, not the wire.
        await using var dbB = _factoryB.CreateDbContext();
        var received = await dbB.Products.SingleAsync();
        Assert.Equal("Cooldrink 330ml", received.Name);
        Assert.Equal(_server.TenantA, received.TenantId);
        Assert.Equal(22m, received.CachedQuantity);
        Assert.Equal(2, await dbB.StockMovements.CountAsync());

        // Applying remote changes must never create outbox rows (no echo loops).
        Assert.Empty(await _clientB.GetPendingItemsAsync(200));

        // And the cursor advanced, so the next pull is empty.
        var newStateB = await _clientB.GetStateAsync();
        Assert.True(newStateB!.LastPullCursor > 0);
    }

    [Fact]
    public async Task NewerLocalEdit_SurvivesStaleRemoteChange()
    {
        await _clientB.InitializeAsync(_server.TenantA, _server.DeviceA2);

        var baseTime = DateTime.UtcNow;
        var localProduct = new Product
        {
            Name = "Local newer name",
            SellPrice = 30m,
            UpdatedAtUtc = baseTime
        };
        await _clientB.SaveLocalWriteAsync(localProduct);

        // A stale change for the same product arrives from the server.
        var stale = new Product
        {
            Id = localProduct.Id,
            Name = "Remote older name",
            SellPrice = 25m,
            UpdatedAtUtc = baseTime.AddHours(-1)
        };
        var change = new SpazaHub.Shared.Sync.SyncChangeDto(
            1, nameof(Product), stale.Id,
            SpazaHub.Shared.Sync.SyncJson.Serialize(stale, typeof(Product)), DateTime.UtcNow);

        await _clientB.ApplyRemoteChangesAsync([change], 1);

        await using var db = _factoryB.CreateDbContext();
        var product = await db.Products.SingleAsync();
        Assert.Equal("Local newer name", product.Name);
        Assert.Equal(30m, product.SellPrice);
    }
}
