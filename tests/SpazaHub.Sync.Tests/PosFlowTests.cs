using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SpazaHub.Client.Data;
using SpazaHub.Client.Services;
using SpazaHub.Client.Sync;
using SpazaHub.Domain.Entities;
using SpazaHub.Domain.Enums;
using SpazaHub.Domain.Services;

namespace SpazaHub.Sync.Tests;

/// <summary>
/// POS and inventory flows driven through the real client services and LocalStore,
/// asserting both the local bookkeeping and the outbox the sync engine will push.
/// </summary>
public class PosFlowTests : IDisposable
{
    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException();
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

    private readonly TestContextFactory _factory = new();
    private readonly LocalStore _store;
    private readonly PosService _pos;
    private readonly InventoryService _inventory;
    private readonly QuickRingService _quickRing;

    public PosFlowTests()
    {
        var persistence = new OpfsDbPersistence(new StubJsRuntime(), NullLogger<OpfsDbPersistence>.Instance);
        _store = new LocalStore(_factory, persistence, NullLogger<LocalStore>.Instance);
        _pos = new PosService(_store, _factory);
        _inventory = new InventoryService(_store, _factory);
        _quickRing = new QuickRingService(_factory);

        _store.InitializeAsync(Guid.NewGuid(), Guid.NewGuid()).GetAwaiter().GetResult();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Product> CreateProductAsync(
        string name, decimal price, decimal threshold = 0m)
    {
        var product = new Product { Name = name, SellPrice = price, LowStockThreshold = threshold };
        await _inventory.SaveProductAsync(product);
        return product;
    }

    [Fact]
    public async Task CompleteSale_WritesEverythingLocallyWithOrderedOutbox()
    {
        var product = await CreateProductAsync("Bread", 18.50m);
        await _inventory.ReceiveGoodsAsync(product.Id, 10m, 14m);

        var (completed, _) = await _pos.CompleteSaleAsync(
            [new CartLine(product.Id, "Bread", 2m, 18.50m, 14m)], []);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Sales.CountAsync());
        Assert.Equal(1, await db.SaleLines.CountAsync());
        Assert.Equal(1, await db.SalePayments.CountAsync());
        Assert.Equal(2, await db.StockMovements.CountAsync());
        Assert.Equal(1, await db.CashMovements.CountAsync());

        // Cached quantity: 10 received minus 2 sold.
        Assert.Equal(8m, (await db.Products.SingleAsync()).CachedQuantity);

        // The outbox must replay in referential order: sale before its children.
        var outbox = await db.SyncOutbox.OrderBy(o => o.Sequence).ToListAsync();
        int saleIndex = outbox.FindIndex(o => o.EntityType == nameof(Sale));
        int lineIndex = outbox.FindIndex(o => o.EntityType == nameof(SaleLine));
        int paymentIndex = outbox.FindIndex(o => o.EntityType == nameof(SalePayment));
        Assert.True(saleIndex >= 0 && saleIndex < lineIndex && saleIndex < paymentIndex);

        // Cost snapshot rode along on the line.
        Assert.Equal(14m, (await db.SaleLines.SingleAsync()).UnitCostSnapshot);
        Assert.Equal(completed.Sale.Id, (await db.SaleLines.SingleAsync()).SaleId);
    }

    [Fact]
    public async Task CompleteSale_FiresLowStockAlertWhenThresholdCrossed()
    {
        var product = await CreateProductAsync("Milk", 22m, threshold: 5m);
        await _inventory.ReceiveGoodsAsync(product.Id, 6m, 16m);

        var (_, none) = await _pos.CompleteSaleAsync(
            [new CartLine(product.Id, "Milk", 0.5m, 22m, 16m)], []);
        Assert.Empty(none);

        var (_, alerts) = await _pos.CompleteSaleAsync(
            [new CartLine(product.Id, "Milk", 1m, 22m, 16m)], []);

        var alert = Assert.Single(alerts);
        Assert.Equal(product.Id, alert.ProductId);
        Assert.Equal(4.5m, alert.QuantityLeft);
    }

    [Fact]
    public async Task ReceiveGoods_MovesWeightedAverageCost()
    {
        var product = await CreateProductAsync("Rice", 35m);

        await _inventory.ReceiveGoodsAsync(product.Id, 10m, 20m);
        var after = await _inventory.ReceiveGoodsAsync(product.Id, 10m, 30m);

        Assert.Equal(25m, after.WeightedAverageCost);
        Assert.Equal(20m, after.CachedQuantity);

        await using var db = _factory.CreateDbContext();
        var received = await db.StockMovements
            .Where(m => m.Type == StockMovementType.GoodsReceived)
            .ToListAsync();
        Assert.Equal(2, received.Count);
        Assert.All(received, m => Assert.NotNull(m.UnitCost));
    }

    [Fact]
    public async Task Wastage_ReducesStockWithExplicitMovementType()
    {
        var product = await CreateProductAsync("Polony", 30m);
        await _inventory.ReceiveGoodsAsync(product.Id, 5m, 22m);

        var after = await _inventory.AdjustStockAsync(
            product.Id, -2m, StockMovementType.Wastage, "Fridge failed in load-shedding");

        Assert.Equal(3m, after.CachedQuantity);

        await using var db = _factory.CreateDbContext();
        var movement = await db.StockMovements.SingleAsync(m => m.Type == StockMovementType.Wastage);
        Assert.Equal(-2m, movement.Quantity);
    }

    [Fact]
    public async Task VoidSale_RestoresStockAndRefundsCash()
    {
        var product = await CreateProductAsync("Chips", 12.34m);
        await _inventory.ReceiveGoodsAsync(product.Id, 10m, 8m);

        var (completed, _) = await _pos.CompleteSaleAsync(
            [new CartLine(product.Id, "Chips", 1m, 12.34m, 8m)], []);

        var reversal = await _pos.VoidSaleAsync(completed.Sale.Id);

        Assert.Equal(completed.Sale.Id, reversal.Sale.ReversesSaleId);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(10m, (await db.Products.SingleAsync()).CachedQuantity);

        // Cash refund mirrors the rounded 12.30 the customer paid.
        var cashOut = await db.CashMovements.SingleAsync(m => m.Type == CashMovementType.CashRefund);
        Assert.Equal(-12.30m, cashOut.Amount);

        // Voiding twice is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pos.VoidSaleAsync(completed.Sale.Id));
    }

    [Fact]
    public async Task QuickRing_OrdersByLocalSalesVelocity()
    {
        var slow = await CreateProductAsync("Slow seller", 5m);
        var fast = await CreateProductAsync("Fast seller", 5m);
        await _inventory.ReceiveGoodsAsync(slow.Id, 50m, 3m);
        await _inventory.ReceiveGoodsAsync(fast.Id, 50m, 3m);

        await _pos.CompleteSaleAsync([new CartLine(slow.Id, "Slow seller", 1m, 5m, 3m)], []);
        for (int i = 0; i < 3; i++)
        {
            await _pos.CompleteSaleAsync([new CartLine(fast.Id, "Fast seller", 4m, 5m, 3m)], []);
        }

        var ring = await _quickRing.GetQuickRingProductsAsync();

        Assert.Equal("Fast seller", ring[0].Name);
        Assert.Equal("Slow seller", ring[1].Name);
    }

    [Fact]
    public async Task MultiTenderSale_SplitsCashAndCard()
    {
        var product = await CreateProductAsync("Airtime holder", 100m);
        await _inventory.ReceiveGoodsAsync(product.Id, 5m, 90m);

        await _pos.CompleteSaleAsync(
            [new CartLine(product.Id, "Airtime holder", 1m, 100m, 90m)],
            [new TenderInput(PaymentMethod.Card, 60m)]);

        await using var db = _factory.CreateDbContext();
        var payments = await db.SalePayments.ToListAsync();
        Assert.Equal(2, payments.Count);
        Assert.Equal(60m, payments.Single(p => p.Method == PaymentMethod.Card).Amount);
        Assert.Equal(40m, payments.Single(p => p.Method == PaymentMethod.Cash).Amount);

        // Only the cash portion hits the drawer.
        Assert.Equal(40m, (await db.CashMovements.SingleAsync()).Amount);
    }
}
